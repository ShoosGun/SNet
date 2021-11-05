﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

using ServerSide.Utils;

namespace ServerSide.Sockets.Servers
{
    public class Server
    {
        private Listener l;
        
        private Queue<ClientEssentials> ReceivedDataCache;
        private readonly object RDC_lock = new object();
        
        public PacketReceiver packetReceiver { get; private set; }
        
        public Server(int port)
        {
            packetReceiver = new PacketReceiver();
            ReceivedDataCache = new Queue<ClientEssentials>();

            l = new Listener(port);
            l.OnClientConnection += L_OnClientConnection;
            l.OnClientDisconnection += L_OnClientDisconnection;
            l.OnClientReceivedData += L_OnClientReceivedData;
            l.Start();
        }

        private void L_OnClientReceivedData(byte[] dgram, string id)
        {
            lock (RDC_lock)
            {
                ReceivedDataCache.Enqueue(new ClientEssentials(id, dgram));
            }
        }

        private void L_OnClientConnection(string id)
        {
            Console.WriteLine("Novo cliente ID = {0}", id);
            OnNewClient(id);
        }

        private void L_OnClientDisconnection(string id)
        {
            Console.WriteLine("Um cliente se desconectou ID = {0}", id);
            OnClientDisconnection(id);
        }

        private byte[] MakeDataWithHeader(byte[] data, int header)
        {
            byte[] dataWithHeader = new byte[4 + 8 + data.Length];
            Array.Copy(BitConverter.GetBytes(header), 0, dataWithHeader, 0, 4); //Header
            Array.Copy(BitConverter.GetBytes(DateTime.UtcNow.ToBinary()), 0, dataWithHeader, 4, 8); //Send Time
            Array.Copy(data, 0, dataWithHeader, 12, data.Length);
            return dataWithHeader;
        }
        public bool Send(byte[] data, int header, string client) => l.Send(MakeDataWithHeader(data, header), client);

        public void SendAll(byte[] data, int header, params string[] dontSendTo) => l.SendAll(MakeDataWithHeader(data, header), dontSendTo);

        private void ReceivedData(ClientEssentials receivedDGram)
        {
            PacketReader packet = new PacketReader(receivedDGram.DGram);
            try
            {
                int Header = packet.ReadInt32();
                DateTime sendTime = packet.ReadDateTime();
                ReceivedPacketData receivedPacketData = new ReceivedPacketData(receivedDGram.ClientID, sendTime, (DateTime.UtcNow - sendTime).Milliseconds);

                packetReceiver.ReadReceivedPacket(ref packet, Header, receivedPacketData);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao ler dados de {0}: {1} {2}", receivedDGram.ClientID, ex.Source, ex.Message);
            }
        }

        /// <summary>
        ///  Handles the new packets sent by all the clients on the in-game loop
        /// </summary>
        /// <param name="receivedData">Holds the packets in an array separated by the clients ids</param>
        /// <param name="resetDataArrays"></param>
        private void ReceivedData(Queue<ClientEssentials> receivedData)
        {
            int amountOfDGrams = receivedData.Count;
            for (int i =0; i< amountOfDGrams; i++)
            {
                ReceivedData(receivedData.Dequeue());
            }
        }
        
        public void CheckReceivedData()
        {            
            bool RDC_NotLoked = Monitor.TryEnter(RDC_lock, 10);
            try
            {
                if (RDC_NotLoked)
                {
                    ReceivedData(ReceivedDataCache);
                }
            }
            finally
            {
                Monitor.Exit(RDC_lock);
            }
        }

        public void Stop()
        {
            l.Stop();
        }

        public event NewClient OnNewClient;
        public delegate void NewClient(string clientID);
        

        public event ClientDisconnection OnClientDisconnection;
        public delegate void ClientDisconnection (string clientID);        
    }

    public struct ClientEssentials
    {
        public string ClientID;
        public byte[] DGram;

        public ClientEssentials(string clientID, byte[] dgram)
        {
            ClientID = clientID;
            DGram = dgram;
        }
    }
}
