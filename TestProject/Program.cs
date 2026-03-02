using System;
using System.IO;
using System.Net.Sockets;

namespace Program {

    static class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Привет");
            startTCPConnect("localhost",5432);
        }
        static void startTCPConnect(string host, int port)
        {
            try
            {
                Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
             
                sock.Shutdown(SocketShutdown.Both);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
    