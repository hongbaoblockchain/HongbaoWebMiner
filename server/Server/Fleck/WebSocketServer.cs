#define __MonoCS__

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Fleck.Helpers;


using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using System.Security;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Diagnostics;
using System.Reflection;


namespace Fleck
{
    public class WebSocketServer : IWebSocketServer
    {
        private readonly string _scheme;
        private readonly IPAddress _locationIP;
        private Action<IWebSocketConnection> _config;

		private const int BytesPerLong = 4; // 32 / 8
		private const int BitsPerByte = 8;



        public WebSocketServer(string location)
        {
            var uri = new Uri(location);
            Port = uri.Port;
            Location = location;
            _locationIP = ParseIPAddress(uri);
            _scheme = uri.Scheme;
            var socket = new Socket(_locationIP.AddressFamily, SocketType.Stream, ProtocolType.IP);
            if(!MonoHelper.IsRunningOnMono()){
                  #if __MonoCS__
                  #else
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                  #endif
            }
            ListenerSocket = new SocketWrapper(socket);
            SupportedSubProtocols = new string[0];
        }

        public ISocket ListenerSocket { get; set; }
        public string Location { get; private set; }
        public int Port { get; private set; }
        public X509Certificate2 Certificate { get; set; }
        public SslProtocols EnabledSslProtocols { get; set; }
        public IEnumerable<string> SupportedSubProtocols { get; set; }
        public bool RestartAfterListenError {get; set; }

        public bool IsSecure
        {
            get { return _scheme == "wss" && Certificate != null; }
        }

        public void Dispose()
        {
            ListenerSocket.Dispose();
        }

        private IPAddress ParseIPAddress(Uri uri)
        {
            string ipStr = uri.Host;

            if (ipStr == "0.0.0.0" ){
                return IPAddress.Any;
            }else if(ipStr == "[0000:0000:0000:0000:0000:0000:0000:0000]")
            {
                return IPAddress.IPv6Any;
            } else {
                try {
                    return IPAddress.Parse(ipStr);
                } catch (Exception ex) {
                    throw new FormatException("Failed to parse the IP address part of the location. Please make sure you specify a valid IP address. Use 0.0.0.0 or [::] to listen on all interfaces.", ex);
                }
            }
        }

        public void Start(Action<IWebSocketConnection> config)
        {
            var ipLocal = new IPEndPoint(_locationIP, Port);
            ListenerSocket.Bind(ipLocal);
            ListenerSocket.Listen(1000); // changed from 100 by tl
            Port = ((IPEndPoint)ListenerSocket.LocalEndPoint).Port;
            FleckLog.Info(string.Format("Server started at {0} (actual port {1})", Location, Port));
            if (_scheme == "wss")
            {
                if (Certificate == null)
                {
                    FleckLog.Error("Scheme cannot be 'wss' without a Certificate");
                    return;
                }

                if (EnabledSslProtocols == SslProtocols.None)
                {
					EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls;
                    //EnabledSslProtocols = SslProtocols.Tls; // changed by tl
                    FleckLog.Debug("Using default TLS 1.0 security protocol.");
                }
            }
            ListenForClients();
            _config = config;
        }

        private void ListenForClients()
        {
            ListenerSocket.Accept(OnClientConnect, e => {
                FleckLog.Error("Listener socket is closed", e);
                if(RestartAfterListenError){
                    FleckLog.Info("Listener socket restarting");
                    try
                    {
                        ListenerSocket.Dispose();
                        var socket = new Socket(_locationIP.AddressFamily, SocketType.Stream, ProtocolType.IP);
                        ListenerSocket = new SocketWrapper(socket);
                        Start(_config);
                        FleckLog.Info("Listener socket restarted");
                    }
                    catch (Exception ex)
                    {
                        FleckLog.Error("Listener could not be restarted", ex);
                    }
                }
            });
        }



		object obj = new object();

        private void OnClientConnect(ISocket clientSocket)
        {
            if (clientSocket == null) return; // socket closed

			// experimental removed by tl
            //FleckLog.Debug(String.Format("Client connected from {0}:{1}", clientSocket.RemoteIpAddress, clientSocket.RemotePort.ToString()));
			//Console.WriteLine(String.Format("Client connected from {0}:{1}", clientSocket.RemoteIpAddress, clientSocket.RemotePort.ToString()));

			string rep = string.Empty;

			bool failed = false;

			try {
				rep = clientSocket.RemoteIpAddress;
				Console.WriteLine("Connecting: " + rep);
			}
			catch{
				Console.WriteLine ("Started but IP not available.");
				failed = true;
			}
				
            ListenForClients();

			if (failed) {
				try{ clientSocket.Close (); }catch{}
				try{ clientSocket.Stream.Close();}catch{}
				try{ clientSocket.Dispose ();}catch{}
				
				return;
			}
				

            WebSocketConnection connection = null;

            connection = new WebSocketConnection(
                clientSocket,
                _config,
                bytes => RequestParser.Parse(bytes, _scheme),
                r => HandlerFactory.BuildHandler(r,
                                                 s => connection.OnMessage(s),
                                                 connection.Close,
                                                 b => connection.OnBinary(b),
                                                 b => connection.OnPing(b),
                                                 b => connection.OnPong(b)),
                s => SubProtocolNegotiator.Negotiate(SupportedSubProtocols, s));

            if (IsSecure)
            {
                FleckLog.Debug("Authenticating Secure Connection");
                clientSocket
                    .Authenticate(Certificate,
                                  EnabledSslProtocols,
						() => 
						{
							Console.WriteLine("Authenticated {0}", rep);
							Server.Firewall.Update(rep, Server.Firewall.UpdateEntry.AuthSuccess);
							connection.StartReceiving();
						}
						,e =>
						{
							FleckLog.Warn("Failed to Authenticate " + rep, e);
							// here we could add connection.Close() ! TL
							Server.Firewall.Update(rep, Server.Firewall.UpdateEntry.AuthFailure);
							connection.Close();
						});
            }
            else
            {
				Server.Firewall.Update(rep, Server.Firewall.UpdateEntry.AuthSuccess);

                connection.StartReceiving();
            }
        }
    }
}
