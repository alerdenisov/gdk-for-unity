using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Improbable.GDK.EditorDiscovery
{
    internal class ServerListenThread
    {
        private readonly int packetReceiveTimeoutMs;
        private readonly int editorDiscoveryPort;
        private readonly ManualResetEvent killTrigger;
        private readonly ConcurrentQueue<string> serverNameQueue;

        private string serverName;

        private readonly string dataPath;
        private readonly string companyName;
        private readonly string productName;

        internal ServerListenThread(
            string serverName,
            int editorDiscoveryPort,
            int packetReceiveTimeoutMs,
            ManualResetEvent killTrigger,
            ConcurrentQueue<string> serverNameQueue,
            string dataPath,
            string companyName,
            string productName)
        {
            this.serverName = serverName;
            this.packetReceiveTimeoutMs = packetReceiveTimeoutMs;
            this.editorDiscoveryPort = editorDiscoveryPort;
            this.killTrigger = killTrigger;
            this.serverNameQueue = serverNameQueue;

            this.dataPath = dataPath;
            this.companyName = companyName;
            this.productName = productName;
        }

        internal void Start()
        {
            using (var client = new UdpClient())
            {
                var socket = client.Client;

                // Allows multiple server listen threads to listen on the same port
                // e.g. multiple Unity editor instances in the same computer.
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, editorDiscoveryPort));

                try
                {
                    while (true)
                    {
                        var tickResult = Tick(client);

                        if (tickResult == TickResult.ReceivedPacket)
                        {
                            continue;
                        }

                        if (tickResult == TickResult.Killed)
                        {
                            return;
                        }

                        throw new ArgumentOutOfRangeException();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private enum TickResult
        {
            ReceivedPacket,
            Killed
        }

        private TickResult Tick(UdpClient client)
        {
            var receiveHandle = new CancellablePacketReceiver(client, packetReceiveTimeoutMs, killTrigger);

            // Wait for a packet or a kill
            while (true)
            {
                var receiveResult = receiveHandle.Poll(out var remoteEp, out var receivedBytes);

                if (receiveResult == CancellablePacketReceiver.ReceiveResult.Success)
                {
                    Debug.Log(
                        $">>>>> Rec: {Encoding.ASCII.GetString(receivedBytes)} from {remoteEp.Address} {remoteEp.Port}");

                    UpdateServerName();

                    var serverInfo = new EditorDiscoveryResponse
                    {
                        ServerName = serverName,
                        CompanyName = companyName,
                        ProductName = productName,
                        DataPath = dataPath,
                    };

                    ServerResponseThread.StartThread(serverInfo, remoteEp);
                    return TickResult.ReceivedPacket;
                }

                if (receiveResult == CancellablePacketReceiver.ReceiveResult.Cancelled)
                {
                    // TODO handle kill

                    client.Close();
                    receiveHandle.ForceEnd();

                    Debug.Log("Killed?");
                    return TickResult.Killed;
                }

                if (receiveResult == CancellablePacketReceiver.ReceiveResult.TimedOut)
                {
                    continue;
                }

                // Unknown receive result
                throw new ArgumentOutOfRangeException();
            }
        }

        private void UpdateServerName()
        {
            // TODO use a mutex instead?
            // TODO google "thread-safe string C#"
            while (!serverNameQueue.IsEmpty)
            {
                if (serverNameQueue.TryDequeue(out var newServerName))
                {
                    serverName = newServerName;
                }
            }
        }

        public static ServerListenThreadHandle StartThread(string serverName, int editorDiscoveryPort,
            int packetReceiveTimeoutMs)
        {
            var handle = new ServerListenThreadHandle(serverName, editorDiscoveryPort, packetReceiveTimeoutMs);

            handle.Start();

            return handle;
        }
    }
}
