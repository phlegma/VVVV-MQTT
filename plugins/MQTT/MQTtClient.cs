#region usings
//vvvv related
using System;
using System.ComponentModel.Composition;
using VVVV.PluginInterfaces.V2;
using VVVV.Core.Logging;

//functionality related
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
#endregion usings

namespace VVVV.Nodes.MQTT
{
    /// <summary>
    /// base class setting up mqtt-client connection to the broker
    /// including vvvv plugininterfacing
    /// </summary>
    /// 
    #region PluginInfo
    [PluginInfo(Name = "Client",
                Category = "Network",
                Version = "MQTT",
                Help = "Client for communicating via the MQTT protocol",
                Tags = "IoT, MQTT", Credits = "M2MQTT m2mqtt.wordpress.com, Jochen Leinberger, explorative-environments.net",
                Author = "woei",
                Bugs = "receiving delete retained message command",
                AutoEvaluate = true)]
    #endregion PluginInfo
    public class MQTTClient: IPluginEvaluate, IDisposable, IPartImportsSatisfiedNotification
    {
        /// <summary>
        /// Quality of Service enum
        /// directly maps to to byte flags of the mqtt specification
        /// </summary>

        #region pins
        [Input("ClientID", DefaultString = "v4mqtt", IsSingle = true)]
        public IDiffSpread<string> FInClientId;

        [Input("Broker URL", DefaultString = "localhost", IsSingle = true)]
        public IDiffSpread<string> FInBrokerAdress;

        [Input("Port", DefaultValue = 1883, IsSingle = true)]
        public IDiffSpread<int> FInPort;

        [Input("Username", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<string> FInUsername;

        [Input("Password", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<string> FInPassword;

        [Input("Clean Session", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<bool> FInSession;

        [Input("Keep Alive Period", DefaultValue = 60, MinValue = 1, IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<int> FInKeepAlive;

        [Input("Will Flag", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<bool> FInWillFlag;

        [Input("Will Topic", DefaultString = "will", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<string> FInWillTopic;

        [Input("Will Message", DefaultString = "disappeared...", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<string> FInWillMessage;

        [Input("Will QoS Level", DefaultEnumEntry = "QoS_1", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<QOS> FInWillQOS;

        [Input("Will Retain", IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        public IDiffSpread<bool> FInWillRetain;

        [Input("Enabled", IsSingle = true)]
        public IDiffSpread<bool> FInEnabled;

        [Output("Client")]
        public ISpread<MqttClient> FOutClient;

        [Output("Connection Status")]
        public ISpread<string> FOutConnectionStatus;

        [Output("Connected")]
        public ISpread<bool> FOutIsConnected;
        #endregion pins

        #region fields
        [Import()]
        public ILogger FLogger;

         MqttClient FClient = null;
         System.Text.UTF8Encoding UTF8Enc = new System.Text.UTF8Encoding();

         bool FNewSession = false;
         bool FDisabled = false;
        #endregion fields

        public void OnImportsSatisfied()
        {
            FOutConnectionStatus[0] = PrependTime("Not connected");
        }

        #region dispose
        public virtual void Dispose()
        {
            try
            {
                TryDisconnect();
            }
            catch (Exception e)
            {
                FLogger.Log(e);
            }
        }
        #endregion dispose

        public void Evaluate(int spreadMax)
        {
            if ((!FInEnabled[0]) && FInEnabled.IsChanged)
            {
                TryDisconnect();
                FDisabled = true;
            }
            else if ((FInEnabled[0]) && 
                    ((FInClientId.IsChanged || FInBrokerAdress.IsChanged || FInPort.IsChanged || FInEnabled.IsChanged) ||
                    (FInWillFlag.IsChanged) ||
                    (FInWillFlag[0] && (FInWillTopic.IsChanged || FInWillMessage.IsChanged || FInWillQOS.IsChanged || FInWillRetain.IsChanged)) ||
                    FInUsername.IsChanged || FInPassword.IsChanged || FInSession.IsChanged || FInKeepAlive.IsChanged))
            {
                Task.Run(() =>
                    {
                        TryDisconnect();

                        if ((FClient == null) || (FInBrokerAdress.IsChanged || FInPort.IsChanged || FInEnabled.IsChanged || FInSession.IsChanged || FInKeepAlive.IsChanged))
                            TryInitialize();

                        TryConnect();
                    });
            }
        }


        /// <summary>
        /// Disconnects the client & removes the delegates from the events
        /// </summary>
        /// <returns>true if operation was successful</returns>
        private bool TryDisconnect()
        {
            if ((FClient != null) && FClient.IsConnected)
            {
                try
                {
                    FClient.Disconnect();

                    if (!FClient.IsConnected)
                    {
                        FOutIsConnected[0] = FClient.IsConnected;
                        FOutConnectionStatus[0] = PrependTime("Disconnected from broker");
                        FClient.ConnectionClosed -= FClient_ConnectionClosed;
                        return true;
                    }
                    else
                        return false;
                }
                catch (Exception e)
                {
                    FLogger.Log(e);
                    FOutConnectionStatus[0] = PrependTime("Failed to disconnect from broker");
                    return false;
                }
            }
            else
                return false;
        }

        /// <summary>
        /// initializes the mqttclient & hooks up to available events
        /// </summary>
        /// <returns>true if operation was successful</returns>
        private bool TryInitialize()
        {
            try
            {
                FClient = new MqttClient(FInBrokerAdress[0], FInPort[0], false, null);

                FOutConnectionStatus[0] = PrependTime("Initialize client for broker: " + FInBrokerAdress[0] + " at Port: " + FInPort[0]);
                return true;
            }
            catch (Exception e)
            {
                FLogger.Log(e);
                FOutIsConnected[0] = false;
                FOutConnectionStatus[0] = PrependTime("Failed to initialize client for broker: " + FInBrokerAdress[0] + " at Port: " + FInPort[0]);
                return false;
            }
        }

        /// <summary>
        /// connects to the broker using simplest connection method overload for compatibility
        /// </summary>
        /// <returns>true if operation was successful</returns>
        private bool TryConnect()
        {
            FOutConnectionStatus[0] = PrependTime("Trying to setup client to connect to broker: " + FInBrokerAdress[0] + " at Port: " + FInPort[0] + ".\r\n");
            FOutConnectionStatus[0] += "This might take a moment ...";
            try
            {
                //try using the simplest possible overload for better compatibility
                if ((FInUsername[0] == string.Empty) && (FInPassword[0] == string.Empty) && (!FInSession[0]) && (FInKeepAlive[0] == 60) && (!FInWillFlag[0]))
                    FClient.Connect(FInClientId[0]);
                else if ((!FInSession[0]) && (FInKeepAlive[0] == 60) && (!FInWillFlag[0]))
                    FClient.Connect(FInClientId[0], FInUsername[0], FInPassword[0]);
                else if (!FInWillFlag[0])
                    FClient.Connect(FInClientId[0], FInUsername[0], FInPassword[0], FInSession[0], (ushort)FInKeepAlive[0]);
                else
                    FClient.Connect(FInClientId[0], FInUsername[0], FInPassword[0], FInWillRetain[0], (byte)FInWillQOS[0], FInWillFlag[0], FInWillTopic[0], FInWillMessage[0], FInSession[0], (ushort)FInKeepAlive[0]);

                FOutIsConnected[0] = FClient.IsConnected;
                string statusMsg = FClient.IsConnected ? "Connected to broker" : "Not connected to broker";
                FOutConnectionStatus[0] = PrependTime(statusMsg+": " + FInBrokerAdress[0] + " at Port: " + FInPort[0] + ".\r\n");
                if (!FClient.IsConnected)
                    FOutConnectionStatus[0] += "try leaving more settings on default for compatibility with the broker";

                FNewSession = true;
                FOutClient[0] = FClient;
                return true;
            }
            catch (Exception e)
            {
                FLogger.Log(e);
                FOutIsConnected[0] = false;
                FOutConnectionStatus[0] = PrependTime("Failed to connect to broker: " + FInBrokerAdress[0] + " at Port: " + FInPort[0]);
                return false;
            }
        }

        internal string PrependTime(string input)
        {
            return DateTime.Now.ToString() + ": " + input;
        }

        #region event methods
        /// <summary>
        /// strangely never gets raised
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void FClient_ConnectionClosed(object sender, EventArgs e)
        {
            FOutIsConnected[0] = false;
            FOutConnectionStatus[0] = PrependTime("Disconnected from broker");
            FClient.ConnectionClosed -= FClient_ConnectionClosed;
        }
        #endregion event methods
    }

    public enum QOS { QoS_0, QoS_1, QoS_2, }
}
