﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

using SNet_Server.Utils;

namespace SNet_Server.Sockets
{
    public class Listener
    {
        private ClientDicitionary Clients;

        private const int DELTA_TIME_OF_VERIFICATION_LOOP = 1000;
        private const int MAX_WAITING_TIME_FOR_CONNECTION_VERIFICATION = 2000;
        private const int MAX_WAITING_TIME_FOR_TIMEOUT = 4000;

        public bool Listening
        {
            get;
            private set;
        }

        public int Port
        {
            get;
            private set;
        }

        private EndPoint AllowedClients;

        private Socket s;
        private const int DATAGRAM_MAX_SIZE = 1284;

        public Listener(int port)
        {
            Port = port;
            s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Clients = new ClientDicitionary();
        }
        public void Start(bool anyConnections = true)
        {
            if (Listening)
                return;

            s.Bind(new IPEndPoint(0, Port));

            if (anyConnections)
            {
                AllowedClients = new IPEndPoint(IPAddress.Any, 0);

                string localIPv4 = GetLocalIPAddress();
                if (localIPv4 != null)
                    Console.WriteLine("Server IP = {0}:{1} <<", localIPv4, Port);
                else
                    Console.WriteLine("Não conseguimos o IPv4");
            }
            else
                AllowedClients = new IPEndPoint(IPAddress.Parse("127.1.0.0"), 0);

            Listening = true;

            //Loop de verificação de informações que dependem de tempo
            Util.RepeatDelayedAction(DELTA_TIME_OF_VERIFICATION_LOOP, DELTA_TIME_OF_VERIFICATION_LOOP, () => TimedVerificationsLoop());

            byte[] nextDatagramBuffer = new byte[DATAGRAM_MAX_SIZE];
            s.BeginReceiveFrom(nextDatagramBuffer, 0, nextDatagramBuffer.Length, SocketFlags.None, ref AllowedClients, ReceiveCallback, nextDatagramBuffer);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                byte[] datagramBuffer = (byte[])ar.AsyncState;
                int datagramSize = s.EndReceiveFrom(ar, ref sender);

                if (datagramSize > 0)
                {
                    switch ((PacketTypes)datagramBuffer[0])
                    {
                        case PacketTypes.CONNECTION:
                            Connection(datagramBuffer, (IPEndPoint)sender);
                            break;
                        case PacketTypes.PACKET:
                            Receive(datagramBuffer, (IPEndPoint)sender);
                            break;
                        case PacketTypes.RELIABLE_RECEIVED:
                            ReceiveReliable_Receive(datagramBuffer, (IPEndPoint)sender);
                            break;
                        case PacketTypes.RELIABLE_SEND:
                            ReceiveReliable_Send(datagramBuffer, (IPEndPoint)sender);
                            break;
                        case PacketTypes.DISCONNECTION:
                            Disconnections((IPEndPoint)sender);
                            break;
                    }
                }
                //Diz que o cliente ainda está conectado
                Clients.TrySetDateTime((IPEndPoint)sender, DateTime.UtcNow);

                byte[] nextDatagramBuffer = new byte[DATAGRAM_MAX_SIZE];
                s.BeginReceiveFrom(nextDatagramBuffer, 0, nextDatagramBuffer.Length, SocketFlags.None, ref AllowedClients, ReceiveCallback, nextDatagramBuffer);
            }
            catch (Exception ex)
            {
                if (ex.GetType() != typeof(SocketException))
                    Console.WriteLine("Erro no callback ao receber dados do Client {0}: {1}\n\t{2}", ((IPEndPoint)sender).Address, ex.Source, ex.Message);
                else
                {
                    Console.WriteLine("Erro com socket tipo {0}: {1}\n\t{2}", ((SocketException)ex).ErrorCode, ex.Source, ex.Message);
                    Disconnections((IPEndPoint)sender, DisconnectionType.ClosedByUser, false);
                }

                byte[] nextDatagramBuffer = new byte[DATAGRAM_MAX_SIZE];
                s.BeginReceiveFrom(nextDatagramBuffer, 0, nextDatagramBuffer.Length, SocketFlags.None, ref AllowedClients, ReceiveCallback, nextDatagramBuffer);
            }
        }

        private bool TimedVerificationsLoop()
        {
            DateTime currentVerificationTime = DateTime.UtcNow;

            Clients.FreeLockedManipulation((clients, ipMaps, reliablePackets) =>
            {
                List<IPEndPoint> clientsToDisconnect = new List<IPEndPoint>();

                //1 - Checar se os clientes não estão passiveis de receber timeout (novas conecções ou não)
                //Fazer checagem se ele pode receber timeout
                foreach (Client client in clients.Values)
                {
                    int timeDifference = (currentVerificationTime - client.TimeOfLastPacket).Milliseconds;

                    if (client.IsConnected)
                    {
                        if (timeDifference > MAX_WAITING_TIME_FOR_TIMEOUT) //Dar timeout
                        {
                            clientsToDisconnect.Add(client.IpEndpoint);
                        }
                        else if (timeDifference > MAX_WAITING_TIME_FOR_TIMEOUT / 2) //Dar aviso que ele poderá receber timeout
                        {
                            //Pela maneira em que está programado a resposta do cliente para mensagens com CONNECTION podemos reutiliza-las como maneira de verificar se estão conectados
                            s.SendTo(BitConverter.GetBytes((byte)PacketTypes.CONNECTION), client.IpEndpoint);
                        }
                    }
                    else if (timeDifference > MAX_WAITING_TIME_FOR_CONNECTION_VERIFICATION) //Dar timeout por não ter respondido a tempo
                    {
                        clientsToDisconnect.Add(client.IpEndpoint);
                    }
                }

                //Dar timeout para todos os clientes que precisam
                for (int i = 0; i < clientsToDisconnect.Count; i++)
                    Disconnections(clientsToDisconnect[i], DisconnectionType.TimedOut);

                clientsToDisconnect.Clear();

                //2 - Checar se precisa enviar novas mensagens que sejam reliable
                List<ReliablePacket> packetsToRemove = new List<ReliablePacket>();
                foreach (ReliablePacket packet in reliablePackets)
                {
                    if (packet.ClientsLeftToReceive.Count <= 0)
                    {
                        packetsToRemove.Add(packet);
                    }
                    else
                    {
                        for (int i = 0; i < packet.ClientsLeftToReceive.Count; i++)
                            SendReliable(packet.Data, packet.PacketID, packet.ClientsLeftToReceive[i]);
                    }
                }
                for (int i = 0; i < packetsToRemove.Count; i++)
                    reliablePackets.Remove(packetsToRemove[i]);
            });
            return !Listening; //Vai parar esse loop se listener estiver desativado
        }

        //Cliente -> Servidor -> Cliente -> Servidor
        private void Connection(byte[] dgram, IPEndPoint sender)
        {
            bool clientExists = Clients.TryManipulateClient(sender, (client) =>
            {
                if (!client.IsConnected)
                    OnClientConnection?.Invoke(client.ID);

                //Se esse cliente existe então podemos definir que ele está sim conectado
                client.IsConnected = true;
            });

            if (clientExists)
                return;

            //Se não existia antes quer dizer que é um novo cliente, fazer o processo de enviar e receber o pedido e gravalo como um cliente não conectado
            Clients.Add(Guid.NewGuid().ToString(), sender);

            byte[] awnserBuffer = new byte[5];
            awnserBuffer[0] = (byte)PacketTypes.CONNECTION;
            Array.Copy(BitConverter.GetBytes(MAX_WAITING_TIME_FOR_TIMEOUT), 0, awnserBuffer, 1, 4);
            s.SendTo(awnserBuffer, sender);

            //Usar aqui possiveis dados que vieram com o dgram            
        }

        //Cliente -> Servidor -> Cliente
        private void Disconnections(IPEndPoint sender, DisconnectionType disconnectionType = DisconnectionType.ClosedByUser, bool sendDisconectionMessage = true)
        {
            //Se o cliente está mesmo conectado então enviar que desconectou mesmo e uma verificação de tal fato
            if (Clients.Remove(sender, out Client client))
            {
                OnClientDisconnection?.Invoke(client.ID, disconnectionType);

                if (sendDisconectionMessage)
                {
                    byte[] awnserBuffer = BitConverter.GetBytes((byte)PacketTypes.DISCONNECTION);
                    s.SendTo(awnserBuffer, sender);
                }
            }
        }

        private void Receive(byte[] dgram, IPEndPoint sender)
        {
            string clientID = "";
            bool result = Clients.TryManipulateClient(sender, (client) =>
            {
                //Se o cliente está mesmo conectado então podemos receber dados dele
                if (client.IsConnected)
                    clientID = client.ID;
            });

            if (!result)
                return;

            byte[] treatedDGram = new byte[dgram.Length - 1];
            Array.Copy(dgram, 1, treatedDGram, 0, treatedDGram.Length);
            OnClientReceivedData?.Invoke(treatedDGram, clientID);

        }

        private void ReceiveReliable_Send(byte[] dgram, IPEndPoint sender)
        {
            string clientID = "";
            bool result = Clients.TryManipulateClient(sender, (client) =>
            {
                //Se o cliente está mesmo conectado então podemos receber dados dele
                if (client.IsConnected)
                    clientID = client.ID;
            });

            if (!result)
                return;

            //Resposta de que recebemos o pacote reliable
            byte[] awnserBuffer = new byte[5];
            awnserBuffer[0] = (byte)PacketTypes.RELIABLE_RECEIVED; //Header de ter recebido
            Array.Copy(dgram, 1, awnserBuffer, 1, 4); //ID da mensagem recebida
            s.SendTo(awnserBuffer, sender);
            // --

            byte[] treatedDGram = new byte[dgram.Length - 5];
            Array.Copy(dgram, 5, treatedDGram, 0, treatedDGram.Length);
            OnClientReceivedData?.Invoke(treatedDGram, clientID);
        }

        private void ReceiveReliable_Receive(byte[] dgram, IPEndPoint sender)
        {
            Clients.TryManipulateClient(sender, (client) =>
            {
                if (!client.IsConnected)
                    return;

                int packeID = BitConverter.ToInt32(dgram, 1);
                if (client.ReliablePacketsToReceive.Remove(packeID, out ReliablePacket packet))
                    packet.ClientReceived(client.ID);
            });
        }

        public void SendAll(byte[] dgram, params string[] dontSendTo)
        {
            byte[] dataGramToSend = new byte[dgram.Length + 1];
            dataGramToSend[0] = (byte)PacketTypes.PACKET;
            Array.Copy(dgram, 0, dataGramToSend, 1, dgram.Length);

            Clients.ClientForEach((c) =>
            {
                if (!dontSendTo.Contains(c.ID) && c.IsConnected)
                    s.SendTo(dataGramToSend, c.IpEndpoint);
            });
        }

        public bool Send(byte[] dgram, string client)
        {
            if (dgram.Length >= DATAGRAM_MAX_SIZE)
                return false;

            IPEndPoint clientIP = null;
            bool result = Clients.TryManipulateClient(client, (clientData) =>
            {
                //Se o cliente está mesmo conectado então podemos receber dados dele
                if (clientData.IsConnected)
                    clientIP = clientData.IpEndpoint;
            });

            if (!result)
                return false;

            //Adicionar o header PACKET na frente da mensagem
            byte[] dataGramToSend = new byte[dgram.Length + 1];
            dataGramToSend[0] = (byte)PacketTypes.PACKET;
            Array.Copy(dgram, 0, dataGramToSend, 1, dgram.Length);

            s.SendTo(dataGramToSend, clientIP);

            return true;
        }

        public bool SendAllReliable(byte[] dgram, params string[] dontSendTo)
        {
            if (dgram.Length >= DATAGRAM_MAX_SIZE)
                return false;

            Clients.FreeLockedManipulation((clients, ipMaps, reliablePackets) =>
            {
                ReliablePacket packet = reliablePackets.Add(dgram);

                foreach (Client c in clients.Values)
                {
                    if (!dontSendTo.Contains(c.ID) && c.IsConnected)
                    {
                        c.ReliablePacketsToReceive.Add(packet.PacketID, packet);
                        SendReliable(dgram, packet.PacketID, c.ID);
                    }
                }

                //Se não tem ninguem para receber pode remover
                if (packet.ClientsLeftToReceive.Count <= 0)
                    reliablePackets.Remove(packet);
            });
            return true;
        }
        public bool SendReliable(byte[] dgram, string client)
        {
            if (dgram.Length < DATAGRAM_MAX_SIZE)
            {
                Clients.FreeLockedManipulation((clients, ipMaps, reliablePackets) =>
                {
                    if (clients.TryGetValue(client, out Client clientData))
                    {
                        ReliablePacket packet = reliablePackets.Add(dgram, clientData.ID);
                        clientData.ReliablePacketsToReceive.Add(packet.PacketID, packet);
                        SendReliable(dgram, packet.PacketID, clientData.ID);
                    }
                });
                return true;
            }
            return false;
        }
        //Client -> Server -> Client
        private void SendReliable(byte[] dgram, int packetID, string client)
        {
            byte[] dataGramToSend = new byte[dgram.Length + 1 + 4];
            dataGramToSend[0] = (byte)PacketTypes.RELIABLE_SEND; // Header
            Array.Copy(BitConverter.GetBytes(packetID), 0, dataGramToSend, 1, 4); // Packet ID para reliable packet

            Array.Copy(dgram, 0, dataGramToSend, 5, dgram.Length);
            s.SendTo(dataGramToSend, Clients.GetClient(client).IpEndpoint);
        }

        public void Stop()
        {
            if (!Listening)
                return;
            Listening = false;

            byte[] disconnectionBuffer = BitConverter.GetBytes((byte)PacketTypes.DISCONNECTION);

            Clients.ClientForEach((c) =>
            {
                s.SendTo(disconnectionBuffer, c.IpEndpoint);
            });

            s.Close();
            Clients.Clear();
        }

        public delegate void ClientConnection(string id);
        public delegate void ClientDisconnection(string id, DisconnectionType reason);
        public delegate void ClientReceivedData(byte[] dgram, string id);

        public event ClientConnection OnClientConnection;
        public event ClientDisconnection OnClientDisconnection;
        public event ClientReceivedData OnClientReceivedData;

        /// <summary>
        /// Returns the local IP
        /// </summary>
        /// <param name="addressFamily">Defaults to IPv4</param>
        /// <returns>If no IP from that AddressFamily is found, returns an empty string</returns>
        public static string GetLocalIPAddress(AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            IPAddress[] IPArray = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
            foreach (var ip in IPArray)
            {
                if (ip.AddressFamily == addressFamily)
                {
                    return ip.ToString();
                }
            }
            return "";
        }
    }
    enum PacketTypes : byte
    {
        CONNECTION,
        PACKET,
        DISCONNECTION,
        RELIABLE_SEND,
        RELIABLE_RECEIVED
    }
}