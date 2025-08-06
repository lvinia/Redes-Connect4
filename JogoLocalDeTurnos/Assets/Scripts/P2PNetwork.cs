using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using UnityEngine;

    public class P2PNetwork
    {
        private TcpListener server;
        private int listenPort;
        private Thread listenThread;

        public Action<int> OnMoveReceived;

        public P2PNetwork(int port)
        {
            listenPort = port;
            StartServer();
        }

        void StartServer()
        {
            listenThread = new Thread(() => {
                server = new TcpListener(IPAddress.Any, listenPort);
                server.Start();
                Debug.Log("[P2P] Servidor ouvindo na porta " + listenPort);

                while (true)
                {
                    TcpClient client = server.AcceptTcpClient();
                    NetworkStream stream = client.GetStream();

                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);

                    string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Debug.Log("[P2P] Jogada recebida: " + msg);

                    if (int.TryParse(msg, out int col))
                    {
                        OnMoveReceived?.Invoke(col);
                    }

                    stream.Close();
                    client.Close();
                }
            });

            listenThread.IsBackground = true;
            listenThread.Start();
        }

        public void SendMove(string ip, int port, int column)
        {
            try
            {
                TcpClient client = new TcpClient();
                client.Connect(ip, port);

                NetworkStream stream = client.GetStream();
                byte[] message = Encoding.UTF8.GetBytes(column.ToString());
                stream.Write(message, 0, message.Length);

                Debug.Log("[P2P] Jogada enviada: " + column);

                stream.Close();
                client.Close();
            }
            catch (Exception e)
            {
                Debug.LogError("[P2P] Erro ao enviar jogada: " + e.Message);
            }
        }

        public void StopServer()
        {
            server?.Stop();
            listenThread?.Abort();
        }
    }