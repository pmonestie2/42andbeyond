using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One deliberately published sandbox login.</summary>
    public sealed class DemoAccount
    {
        public readonly string Code, ClassName, Username;
        public string Password => Username;

        /// <summary>Keep only a public class code and its documented demo login.</summary>
        public DemoAccount(string code, string className)
        { Code = code; ClassName = className; Username = "p" + code; }
    }

    /// <summary>Decode the server's narrow published-demo roster.</summary>
    public static class DemoRoster
    {
        private static readonly Dictionary<string, string> ClassNames = new Dictionary<string, string>
        {
            ["te"] = "Terran Enforcer", ["tt"] = "Terran Trader", ["ts"] = "Terran Scout",
            ["jd"] = "Jenquai Defender", ["js"] = "Jenquai Seeker", ["je"] = "Jenquai Explorer",
            ["pw"] = "Progen Warrior", ["pp"] = "Progen Privateer", ["ps"] = "Progen Sentinel",
        };

        /// <returns>
        /// DemoAccount[]: Only the published class accounts in a validated reply.
        /// </returns>
        public static DemoAccount[] Decode(byte[] body)
        {
            Wire.Require(body, 0, 2);
            int count = body[1];
            if (body[0] != 1 || count > 9 || body.Length != 2 + 2 * count)
                throw new InvalidDataException("Invalid published demo roster.");
            var accounts = new DemoAccount[count];
            var seen = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                string code = Wire.Text(body, 2 + 2 * i, 2);
                if (code.Length != 2 || !ClassNames.TryGetValue(code, out string name) || !seen.Add(code))
                    throw new InvalidDataException("Invalid published demo class.");
                accounts[i] = new DemoAccount(code, name);
            }
            return accounts;
        }
    }

    /// <summary>Read the public sandbox roster without blocking Unity's main thread.</summary>
    public sealed class DemoRosterWorker : IDisposable
    {
        private readonly Thread thread;
        private readonly string host;
        private UdpClient socket;
        private DemoAccount[] accounts;
        private string error;
        private int disposed;
        public DemoAccount[] Accounts => Volatile.Read(ref accounts);
        public string Error => Volatile.Read(ref error);

        /// <summary>Start one bounded read-only query of a private server address.</summary>
        public DemoRosterWorker(string host)
        {
            LocalSession.LocalAddress(host);
            this.host = host;
            thread = new Thread(Run) { IsBackground = true, Name = "42 and Beyond demo roster" };
            thread.Start();
        }

        /// <summary>Receive only the documented public class-code reply.</summary>
        private void Run()
        {
            try
            {
                var endpoint = new IPEndPoint(LocalSession.LocalAddress(host), 3806);
                using (var query = new UdpClient(AddressFamily.InterNetwork))
                {
                    Volatile.Write(ref socket, query);
                    if (Volatile.Read(ref disposed) != 0) return;
                    query.Client.ReceiveTimeout = 1800;
                    query.Connect(endpoint);
                    byte[] request = Wire.Encode(0x1002, Array.Empty<byte>(), 0, 0);
                    query.Send(request, request.Length);
                    IPEndPoint sender = null;
                    byte[] reply = query.Receive(ref sender);
                    if (Wire.Datagram(reply, 0) != 0x1003)
                        throw new InvalidDataException("Unexpected published demo reply.");
                    Volatile.Write(ref accounts, DemoRoster.Decode(Wire.Slice(reply, 12, reply.Length - 12)));
                }
            }
            catch (SocketException) { if (Volatile.Read(ref disposed) == 0) Volatile.Write(ref error, "Demo roster unavailable on this server."); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { if (Volatile.Read(ref disposed) == 0) Volatile.Write(ref error, exception.Message); }
            finally { Volatile.Write(ref socket, null); }
        }

        /// <summary>Cancel the outstanding read and join its owned thread.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Volatile.Read(ref socket)?.Close();
            thread.Join();
        }
    }
}
