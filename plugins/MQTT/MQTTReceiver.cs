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
    /// handling sending and receiving of mqtt-client
    /// including vvvv plugininterfacing
    /// </summary>
    #region PluginInfo
    [PluginInfo(Name = "Receiver",
                Category = "Network",
                Version = "MQTT"
                //Help = "Sends MQTT Messages to Broker via Client",
                //Tags = "IoT, MQTT", Credits = "M2MQTT m2mqtt.wordpress.com, Jochen Leinberger, explorative-environments.net",
                //Author = "woei",
                //Bugs = "receiving delete retained message command",
                //AutoEvaluate = true
                )]
    #endregion PluginInfo
    public class MQTTReceiver : IPluginEvaluate, IDisposable
    {
        #region pins
        [Input("Client", IsSingle = true)]
        IDiffSpread<MqttClient> FInClient;

        [Input("Topic", DefaultString = "vvvv/topic")]
        ISpread<string> FInTopic;

        [Input("Qualiy of Service", DefaultEnumEntry = "QoS_0")]
        IDiffSpread<QOS> FInQoS;

        [Input("Remove Retained", IsBang = true)]
        ISpread<bool> FInRemoveRetained;

        [Output("Topic")]
        ISpread<string> FOutTopic;

        [Output("Message")]
        ISpread<string> FOutMessage;

        [Output("Qualiy of Service")]
        ISpread<QOS> FOutQoS;

        [Output("Is Retained")]
        ISpread<bool> FOutIsRetained;

        [Output("On Data")]
        ISpread<bool> FOutOnData;

        [Output("Publish Queue Count", Visibility = PinVisibility.OnlyInspector)]
        ISpread<int> FOutboundCount;

        [Output("Message Status")]
        ISpread<string> FOutMessageStatus;
        #endregion pins

        #region fields
        [Import()]
        public ILogger FLogger;

        private MqttClient FClient = null;
        private System.Text.UTF8Encoding UTF8Enc = new System.Text.UTF8Encoding();
        bool FNewSession = false;
        Queue<string> FMessageStatusQueue = new Queue<string>();

        HashSet<ushort> FPublishStatus = new HashSet<ushort>(); //keeps track received-confirmations of published packets

        Queue<MqttMsgPublishEventArgs> FPacketQueue = new Queue<MqttMsgPublishEventArgs>();  //incoming packets
        HashSet<Tuple<string, QOS>> FSubscriptions = new HashSet<Tuple<string, QOS>>(); //list of current subscriptions
        Dictionary<ushort, Tuple<string, QOS>> FSubscribeStatus = new Dictionary<ushort, Tuple<string, QOS>>(); //matches subscribe commands to packet ids
        Dictionary<ushort, Tuple<string, QOS>> FUnsubscribeStatus = new Dictionary<ushort, Tuple<string, QOS>>(); //matches unsubscribe commands to packet ids
        #endregion fields

        public void Dispose()
        {
            try
            {
                foreach (var tup in FSubscriptions)
                    FClient.Unsubscribe(new string[] { tup.Item1 });
            }
            catch (Exception e)
            {
                FLogger.Log(e);
            }
            Dispose();
        }

        public void Evaluate(int spreadMax)
        {

            if ((FInClient[0] != null) && FInClient.IsChanged)
            {
                FClient = FInClient[0];
                FClient.MqttMsgPublished += FClient_MqttMsgPublished;
                FClient.MqttMsgPublishReceived += FClient_MqttMsgPublishReceived;
                FClient.MqttMsgSubscribed += FClient_MqttMsgSubscribed;
                FClient.MqttMsgUnsubscribed += FClient_MqttMsgUnsubscribed;
            }

            if ((FClient != null) && FClient.IsConnected)
            {
                HashSet<Tuple<string, QOS>> currentSubscriptions = new HashSet<Tuple<string, QOS>>();
                List<Tuple<string, QOS>> newSubscriptions = new List<Tuple<string, QOS>>();
                for (int i = 0; i < spreadMax; i++)
                {
                    //subscription and unsubscription has to be handled outside this loop
                    //unsubscription has to be triggered first (matters in the case of same topic with different qos)
                    #region receiving
                        var tup = new Tuple<string, QOS>(FInTopic[i], FInQoS[i]);
                        currentSubscriptions.Add(tup);

                        if (!FSubscriptions.Remove(tup))
                            newSubscriptions.Add(tup);

                    #endregion receiving
                }

                #region unsubscribe
                try
                {
                    if (FSubscriptions.Count > 0)
                    {
                        foreach (var tuple in FSubscriptions)
                        {
                            var unsubscribeId = FClient.Unsubscribe(new string[] { tuple.Item1 });
                            FUnsubscribeStatus.Add(unsubscribeId, tuple);
                        }
                    }
                    FSubscriptions = new HashSet<Tuple<string, QOS>>(currentSubscriptions);
                }
                catch (Exception e)
                {
                    FLogger.Log(e);
                    foreach (var s in currentSubscriptions)
                        FSubscriptions.Add(s);
                }
                #endregion unsubscribe

                #region subscribe
                if (FNewSession)
                {
                    newSubscriptions.AddRange(FSubscriptions);
                    FNewSession = false;
                }
                foreach (var subs in newSubscriptions)
                {
                    try
                    {
                        var subscribeId = FClient.Subscribe(new string[] { subs.Item1 }, new byte[] { (byte)subs.Item2 });
                        FSubscribeStatus.Add(subscribeId, subs);
                    }
                    catch
                    {
                        FLogger.Log(LogType.Warning, string.Format("couldn't subscribe to {0} with qos {1}", subs.Item1, subs.Item2));
                    }
                }
                #endregion subscribe
            }

            if (FMessageStatusQueue.Count > 0)
            {
                FOutMessageStatus.AssignFrom(FMessageStatusQueue.ToArray());
                FMessageStatusQueue.Clear();
            }

            FOutTopic.AssignFrom(FPacketQueue.Select(x => x.Topic).ToArray());
            FOutMessage.AssignFrom(FPacketQueue.Select(x => UTF8Enc.GetString(x.Message)));
            FOutQoS.AssignFrom(FPacketQueue.Select(x => (QOS)x.QosLevel));
            FOutIsRetained.AssignFrom(FPacketQueue.Select(x => x.Retain));
            FOutOnData[0] = FPacketQueue.Count > 0;
            FPacketQueue.Clear();

            FOutboundCount[0] = FPublishStatus.Count;
        }

        private string PrependTime(string input)
        {
            return DateTime.Now.ToString() + ": " + input;
        }

        #region events
        /// <summary>
        /// confirmation of the broker that a packet was successfully transmitted
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void FClient_MqttMsgPublished(object sender, MqttMsgPublishedEventArgs e)
        {
            FMessageStatusQueue.Enqueue(PrependTime("published message with packet ID " + e.MessageId));
            FPublishStatus.Remove(e.MessageId);
        }

        /// <summary>
        /// passes packets received by the mqtt-client
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">packet data</param>
        public void FClient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            FPacketQueue.Enqueue(e);
            FMessageStatusQueue.Enqueue(PrependTime("received topic " + e.Topic));
        }

        /// <summary>
        /// broker acknowledges a subscription
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void FClient_MqttMsgSubscribed(object sender, MqttMsgSubscribedEventArgs e)
        {
            var issued = FSubscribeStatus[e.MessageId];
            FMessageStatusQueue.Enqueue(PrependTime("subscribed to " + issued.Item1));
            FSubscribeStatus.Remove(e.MessageId);
        }

        /// <summary>
        /// broker acknowledges an unsubscription
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void FClient_MqttMsgUnsubscribed(object sender, MqttMsgUnsubscribedEventArgs e)
        {
            var issued = FUnsubscribeStatus[e.MessageId];
            FMessageStatusQueue.Enqueue(PrependTime("unsubscribed from " + issued.Item1));
            FUnsubscribeStatus.Remove(e.MessageId);
        }
        #endregion events
    }
}
