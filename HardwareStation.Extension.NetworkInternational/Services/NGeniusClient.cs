using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MAF.Commerce.HardwareStation.Extension.NGenius.Services
{
    internal sealed class NGeniusClient : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private TcpClient tcp;
        private NetworkStream stream;

        public NGeniusClient(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public void Connect()
        {
            if (tcp != null && tcp.Connected) return;

            tcp = new TcpClient();
            tcp.Connect(host, port);
            stream = tcp.GetStream();

            // Initial handshake
            Send("connect()");
        }

        public void Disconnect()
        {
            try { stream?.Dispose(); tcp?.Close(); } catch { /* ignore */ }
            stream = null;
            tcp = null;
        }

        public void StartTransaction(JObject payload)
            => Send($"startTransaction {payload.ToString(Newtonsoft.Json.Formatting.None)}");

        public JObject GetStatus()
            => Parse(Send("getStatus()"));

        public JObject GetResult(string sourceId)
            => Parse(Send($"getResult({sourceId})"));

        /// <summary>
        /// Check last transaction result after cable disconnect or app restart.
        /// </summary>
        public JObject CheckLastTransactionResult(string lastSourceId)
        {
            if (string.IsNullOrEmpty(lastSourceId))
                return new JObject();
            Log($"Checking last transaction result for SourceId: {lastSourceId}");
            return GetResult(lastSourceId);
        }

        /// <summary>
        /// Polls PED for transaction completion, handles parameter requests, timeouts, and errors.
        /// </summary>
        public async Task<JObject> PollUntilCompleteAsync(string sourceId, TimeSpan poll, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            bool updateSent = false;
            while (DateTime.UtcNow - start < (updateSent ? TimeSpan.FromSeconds(150) : timeout))
            {
                var status = GetStatus();

                // Error 110: Terminal Busy
                if (status?["error"]?.ToString().Contains("Previous command still in progress") == true)
                {
                    Log($"PED busy (error 110), waiting for PED to be ready. SourceId: {sourceId}");
                    await Task.Delay(poll);
                    continue;
                }
                // Error 101: Command Timed Out
                if (status?["error"]?.ToString().Contains("Command timed out") == true)
                {
                    Log($"PED command timed out (error 101), waiting 15s. SourceId: {sourceId}");
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    continue;
                }

                // Handle PED parameter request
                var parameter = status?["parameter"]?.Value<string>();
                var parameterType = status?["parameterType"]?.Value<string>();

                if (!string.IsNullOrEmpty(parameter) && !string.IsNullOrEmpty(parameterType))
                {
                    string parameterValue = GetDefaultParameterValue(parameter, parameterType, status);

                    var updatePayload = new JObject
                    {
                        ["success"] = false,
                        ["amount"] = status?["amount"],
                        ["cashback"] = status?["cashback"],
                        ["sourceid"] = sourceId,
                        ["currency"] = status?["currency"],
                        ["inProgress"] = status?["inProgress"],
                        ["displayText"] = status?["displayText"],
                        ["parameter"] = parameter,
                        ["parameterType"] = parameterType,
                        ["parameterValue"] = parameterValue
                    };
                    Send($"updateTransaction {updatePayload}");
                    updateSent = true;

                    // Timeout/cancel logic after updateTransaction
                    if (DateTime.UtcNow - start > TimeSpan.FromSeconds(updateSent ? 150 : 90))
                    {
                        Send("cancelTransaction()");
                        Log($"Transaction timeout after updateTransaction, sent cancelTransaction. SourceId: {sourceId}");
                        break;
                    }

                    await Task.Delay(poll);
                    continue;
                }

                // Transaction complete
                if ((bool?)status?["complete"] == true)
                    break;

                await Task.Delay(poll);
            }

            // If not complete after timeout, cancel transaction
            var finalStatus = GetStatus();
            if ((bool?)finalStatus?["complete"] != true)
            {
                Send("cancelTransaction()");
                Log($"Transaction timeout, sent cancelTransaction. SourceId: {sourceId}");
            }

            return GetResult(sourceId);
        }

        private string Send(string line)
        {
            if (stream == null)
                throw new InvalidOperationException("NGPAS stream not initialized. Call Connect() first.");

            Log($"SEND: {line}");

            var outBytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(outBytes, 0, outBytes.Length);

            var buffer = new byte[16384];
            var read = stream.Read(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, read);

            Log($"RECV: {response}");

            if (response.Contains("error"))
                Log($"ERROR: {response}");

            return response;
        }

        private static JObject Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return new JObject();

            if (s.TrimStart().StartsWith("error"))
            {
                Log($"PED Error: {s}");
                int idx = s.IndexOf('{');
                if (idx >= 0)
                {
                    try
                    {
                        return JObject.Parse(s.Substring(idx));
                    }
                    catch (Exception ex)
                    {
                        Log($"PARSE ERROR: {ex} | Raw: {s}");
                        return new JObject { ["error"] = s, ["parseError"] = ex.ToString() };
                    }
                }
                return new JObject { ["error"] = s };
            }

            if (s.TrimStart().StartsWith("transaction"))
            {
                int idx = s.IndexOf('{');
                if (idx >= 0)
                    s = s.Substring(idx);
            }

            try
            {
                return JObject.Parse(s);
            }
            catch (Exception ex)
            {
                Log($"PARSE ERROR: {ex} | Raw: {s}");
                return new JObject { ["parseError"] = ex.ToString(), ["raw"] = s };
            }
        }

        public static void Log(string message)
        {
            System.IO.File.AppendAllText("ngenius.log", $"{DateTime.UtcNow:O} {message}\n");
        }

        public void Dispose() => Disconnect();

        /// <summary>
        /// Provide default values for PED parameters, including checkcard logic.
        /// </summary>
        private string GetDefaultParameterValue(string parameter, string parameterType, JObject status)
        {
            // Handle checkcard parameter as per documentation
            if (parameter.Equals("checkcard", StringComparison.OrdinalIgnoreCase))
            {
                // Example: always continue, or use business logic
                return "continue";
            }
            if (parameterType.Equals("alphanumeric", StringComparison.OrdinalIgnoreCase))
                return "ok";
            if (parameterType.Equals("numeric", StringComparison.OrdinalIgnoreCase))
                return "0";
            if (parameterType.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                return "true";
            return string.Empty;
        }

        /// <summary>
        /// PED is idle if not busy, not in progress, and displayText is NO TXN or SYSTEM IDLE.
        /// </summary>
        public bool IsPedIdle()
        {
            var status = GetStatus();
            var displayText = status?["displayText"]?.Value<string>();
            return status?["inProgress"]?.Value<bool>() == false
                && status?["complete"]?.Value<bool>() == true
                && (displayText?.Contains("NO TXN") == true || displayText?.Contains("SYSTEM IDLE") == true);
        }
    }
}
