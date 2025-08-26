using MAF.Commerce.HardwareStation.Extension.NGenius.Services;
using PSDK = Microsoft.Dynamics.Retail.PaymentSDK.Portable;
using Microsoft.Dynamics.Commerce.HardwareStation.CardPayment;
using Microsoft.Dynamics.Commerce.HardwareStation.PeripheralRequests;
using Microsoft.Dynamics.Commerce.HardwareStation.Peripherals;
using Microsoft.Dynamics.Commerce.HardwareStation;
using Microsoft.Dynamics.Commerce.Runtime.Handlers;
using Microsoft.Dynamics.Commerce.Runtime.Messages;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

public sealed class NGeniusPaymentConnector : INamedRequestHandler
{
    private const string HandlerConst = "NGeniusPaymentTerminal";
    public string HandlerName => HandlerConst;

    private string deviceHost = "localhost";
    private int devicePort = 8085;
    private string mid = "12345678";
    private string tid = "87654321";
    private bool offlineMode = false;

    private SettingsInfo terminalSettings = null;

    public sealed class GetXReportRequest : Request { }
    public sealed class GetZReportRequest : Request { }


    private NGeniusClient ngClient;
    private string lastSourceId; // Persist this between transactions/app restarts

    public IEnumerable<Type> SupportedRequestTypes => new[]
    {
        typeof(OpenPaymentTerminalDeviceRequest),
        typeof(BeginTransactionPaymentTerminalDeviceRequest),
        typeof(UpdateLineItemsPaymentTerminalDeviceRequest),
        typeof(CancelOperationPaymentTerminalDeviceRequest),
        typeof(EndTransactionPaymentTerminalDeviceRequest),
        typeof(ClosePaymentTerminalDeviceRequest),
        typeof(LockPaymentTerminalDeviceRequest),
        typeof(ReleasePaymentTerminalDeviceRequest),
        typeof(AuthorizePaymentTerminalDeviceRequest),
        typeof(CapturePaymentTerminalDeviceRequest),
        typeof(RefundPaymentTerminalDeviceRequest),
        typeof(VoidPaymentTerminalDeviceRequest),
        typeof(FetchTokenPaymentTerminalDeviceRequest),
        typeof(ActivateGiftCardPaymentTerminalRequest),
        typeof(AddBalanceToGiftCardPaymentTerminalRequest),
        typeof(GetGiftCardBalancePaymentTerminalRequest),
        typeof(GetPrivateTenderPaymentTerminalDeviceRequest),
        typeof(ExecuteTaskPaymentTerminalDeviceRequest)
    };

    public Response Execute(Request request)
    {
        Microsoft.Dynamics.Commerce.Runtime.ThrowIf.Null(request, nameof(request));

        switch (request)
        {
            case OpenPaymentTerminalDeviceRequest rOpen:
                Open(rOpen);
                return NullResponse.Instance;

            case BeginTransactionPaymentTerminalDeviceRequest rBegin:
                BeginTransaction(rBegin);
                return NullResponse.Instance;

            case UpdateLineItemsPaymentTerminalDeviceRequest rUpdate:
                UpdateLineItems(rUpdate);
                return NullResponse.Instance;

            case CancelOperationPaymentTerminalDeviceRequest rCancel:
                CancelOperation(rCancel);
                return NullResponse.Instance;

            case EndTransactionPaymentTerminalDeviceRequest rEnd:
                EndTransaction(rEnd);
                return NullResponse.Instance;

            case ClosePaymentTerminalDeviceRequest rClose:
                Close(rClose);
                return NullResponse.Instance;

            case LockPaymentTerminalDeviceRequest _:
            case ReleasePaymentTerminalDeviceRequest _:
                return NullResponse.Instance;

            case AuthorizePaymentTerminalDeviceRequest rAuth:
                return AuthorizePaymentAsync(rAuth).GetAwaiter().GetResult();

            case CapturePaymentTerminalDeviceRequest _:
                return NullResponse.Instance;

            case RefundPaymentTerminalDeviceRequest rRefund:
                return RefundPaymentAsync(rRefund).GetAwaiter().GetResult();

            case VoidPaymentTerminalDeviceRequest rVoid:
                return VoidPaymentAsync(rVoid).GetAwaiter().GetResult();

            case FetchTokenPaymentTerminalDeviceRequest _:
                return NullResponse.Instance;

            case ActivateGiftCardPaymentTerminalRequest _:
            case AddBalanceToGiftCardPaymentTerminalRequest _:
            case GetGiftCardBalancePaymentTerminalRequest _:
            case GetPrivateTenderPaymentTerminalDeviceRequest _:
                return NullResponse.Instance;

            case GetXReportRequest _:
                var xReport = GetXReportAsync().GetAwaiter().GetResult();
                var xLines = GetReceiptLines(xReport).ToList();
                return new StringResponse(string.Join(Environment.NewLine, xLines));

            case GetZReportRequest _:
                var zReport = GetZReportAsync().GetAwaiter().GetResult();
                var zLines = GetReceiptLines(zReport).ToList();
                return new StringResponse(string.Join(Environment.NewLine, zLines));

            case ExecuteTaskPaymentTerminalDeviceRequest _:
                throw new NotSupportedException("Custom payment terminal tasks are not supported by NGenius connector.");
        }

        throw new NotSupportedException($"Request '{request.GetType()}' is not supported by {HandlerConst}.");
    }

    public void Open(OpenPaymentTerminalDeviceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        Utilities.WaitAsyncTask(() => OpenAsync(request.DeviceName, request.TerminalSettings, request.DeviceConfig));
    }

    public async Task OpenAsync(string deviceName, SettingsInfo terminalSettings, PeripheralConfiguration deviceConfig)
    {
        this.terminalSettings = terminalSettings;
        if (this.ngClient == null)
            this.ngClient = new NGeniusClient(this.deviceHost, this.devicePort);

        this.ngClient.Connect();
        await Task.CompletedTask;
    }

    public void BeginTransaction(BeginTransactionPaymentTerminalDeviceRequest request)
    {
        Utilities.WaitAsyncTask(() => Task.CompletedTask);
    }

    public void UpdateLineItems(UpdateLineItemsPaymentTerminalDeviceRequest request)
    {
        Utilities.WaitAsyncTask(() => Task.CompletedTask);
    }

    public void CancelOperation(CancelOperationPaymentTerminalDeviceRequest request)
    {
        Utilities.WaitAsyncTask(() => Task.CompletedTask);
    }

    public void EndTransaction(EndTransactionPaymentTerminalDeviceRequest request)
    {
        Utilities.WaitAsyncTask(() => Task.CompletedTask);
    }

    public void Close(ClosePaymentTerminalDeviceRequest request)
    {
        Utilities.WaitAsyncTask(() => Task.Run(async () =>
        {
            this.ngClient?.Disconnect();
            this.ngClient = null;
            await Task.CompletedTask;
        }));
    }

    // ============================== Typed implementations ==============================

    public async Task<AuthorizePaymentTerminalDeviceResponse> AuthorizePaymentAsync(AuthorizePaymentTerminalDeviceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureClientAsync();

        string sourceId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff").Substring(0, 15);
        lastSourceId = sourceId;

        // Strict idle check: only send startTransaction after getStatus response is received and PED is idle
        while (true)
        {
            var status = ngClient.GetStatus(); // Wait for response
            if (ngClient.IsPedIdle())
                break;
            NGeniusClient.Log("PED busy, waiting before sending transaction...");
            await Task.Delay(3000);
        }

        // Send startTransaction with compact JSON
        ngClient.StartTransaction(new JObject
        {
            ["success"] = false,
            ["amount"] = ToMinorUnits(request.Amount),
            ["sourceid"] = sourceId,
            ["type"] = "eposSale"
        });

        // Poll for completion and get result
        var result = await ngClient.PollUntilCompleteAsync(sourceId, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120));

        var receiptLines = GetReceiptLines(result);
        var isApproved = IsApproved(result);
        var authCode = (string)(result["authCode"] ?? "");
        var rrn = (string)(result["rrn"] ?? "");
        var panMasked = (string)(result["panMasked"] ?? "");
        var providerId = (string)(result["sourceId"] ?? sourceId);

        var info = new PaymentInfo
        {
            IsApproved = isApproved,
            ApprovedAmount = request.Amount,
            CardNumberMasked = panMasked,
            PaymentSdkContentType = PaymentSdkContentType.Authorization,
            PaymentSdkData = BuildPropertiesXml(
                ("AuthCode", authCode),
                ("SourceId", providerId),
                ("RRN", rrn))
        };

        return new AuthorizePaymentTerminalDeviceResponse(info);
    }
    // Apply the same strict sequencing to Refund and Void flows:
    public async Task<RefundPaymentTerminalDeviceResponse> RefundPaymentAsync(RefundPaymentTerminalDeviceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureClientAsync();

        string sourceId = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff").Substring(0, 15);
        lastSourceId = sourceId;

        while (true)
        {
            var status = ngClient.GetStatus();
            if (ngClient.IsPedIdle())
                break;
            NGeniusClient.Log("PED busy, waiting before sending refund transaction...");
            await Task.Delay(3000);
        }

        ngClient.StartTransaction(new JObject
        {
            ["type"] = "eposRefund",
            ["amount"] = ToMinorUnits(request.Amount),
            ["currency"] = request.Currency,
            ["sourceId"] = sourceId,
            ["mid"] = mid,
            ["tid"] = tid,
            ["offline"] = offlineMode
        });

        var result = await ngClient.PollUntilCompleteAsync(sourceId, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120));

        var receiptLines = GetReceiptLines(result);
        var isApproved = IsApproved(result);
        var authCode = (string)(result["authCode"] ?? "");
        var rrn = (string)(result["rrn"] ?? "");
        var panMasked = (string)(result["panMasked"] ?? "");
        var providerId = (string)(result["sourceId"] ?? sourceId);

        var info = new PaymentInfo
        {
            IsApproved = isApproved,
            ApprovedAmount = request.Amount,
            CardNumberMasked = panMasked,
            PaymentSdkContentType = PaymentSdkContentType.Authorization,
            PaymentSdkData = BuildPropertiesXml(
                ("AuthCode", authCode),
                ("SourceId", providerId),
                ("RRN", rrn))
        };

        return new RefundPaymentTerminalDeviceResponse(info);
    }

    public async Task<VoidPaymentTerminalDeviceResponse> VoidPaymentAsync(VoidPaymentTerminalDeviceRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        await EnsureClientAsync();

        var originalSourceId = TryReadPropertyFromXml(request.PaymentPropertiesXml, "SourceId")
                               ?? TryReadFromExtension(request.ExtensionTransactionProperties, "SourceId")
                               ?? string.Empty;
        lastSourceId = originalSourceId;

        while (true)
        {
            var status = ngClient.GetStatus();
            if (ngClient.IsPedIdle())
                break;
            NGeniusClient.Log("PED busy, waiting before sending void transaction...");
            await Task.Delay(3000);
        }

        ngClient.StartTransaction(new JObject
        {
            ["type"] = "eposVoid",
            ["sourceId"] = originalSourceId,
            ["mid"] = mid,
            ["tid"] = tid,
            ["offline"] = offlineMode
        });

        var result = await ngClient.PollUntilCompleteAsync(originalSourceId, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120));

        var receiptLines = GetReceiptLines(result);
        var isApproved = IsApproved(result);
        var authCode = (string)(result["authCode"] ?? "");
        var rrn = (string)(result["rrn"] ?? "");
        var panMasked = (string)(result["panMasked"] ?? "");
        var providerId = (string)(result["sourceid"] ?? originalSourceId);

        var info = new PaymentInfo
        {
            IsApproved = isApproved,
            ApprovedAmount = 0m,
            CardNumberMasked = panMasked,
            PaymentSdkContentType = PaymentSdkContentType.Authorization,
            PaymentSdkData = BuildPropertiesXml(
                ("AuthCode", authCode),
                ("SourceId", providerId),
                ("RRN", rrn))
        };

        return new VoidPaymentTerminalDeviceResponse(info);
    }

    // ============================== Helpers ==============================

    private async Task EnsureClientAsync()
    {
        if (this.ngClient == null)
            this.ngClient = new NGeniusClient(this.deviceHost, this.devicePort);

        this.ngClient.Connect();
        await Task.CompletedTask;
    }

    private static string ToMinorUnits(decimal amount) => ((long)(amount * 100M)).ToString();

    private static bool IsApproved(JObject result)
        => ((bool?)result["success"] == true) && ((bool?)result["declined"] != true);

    private async Task<JObject> RunTransactionAsync(string sourceId, JObject payload)
    {
        try
        {
            // Wait until PED is idle before sending transaction
            while (!this.ngClient.IsPedIdle())
            {
                NGeniusClient.Log("PED busy, waiting before sending transaction...");
                await Task.Delay(3000);
            }

            this.ngClient.StartTransaction(payload);
            return await this.ngClient.PollUntilCompleteAsync(sourceId, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120));
        }
        catch (Exception ex)
        {
            NGeniusClient.Log($"EXCEPTION: {ex}");
            throw new PeripheralException(PeripheralException.PaymentTerminalError, $"NGenius transaction error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// On cable disconnect/app restart, check last transaction result before new transaction.
    /// </summary>
    private async Task CheckLastTransactionResultAsync()
    {
        if (!string.IsNullOrEmpty(lastSourceId))
        {
            var lastResult = this.ngClient.CheckLastTransactionResult(lastSourceId);
            // Optionally handle lastResult if transaction was incomplete
            await Task.CompletedTask;
        }
    }

    private static string BuildPropertiesXml(params (string Name, string Value)[] pairs)
    {
        var list = new List<PSDK.PaymentProperty>
        {
            new PSDK.PaymentProperty(PSDK.Constants.GenericNamespace.Connector, PSDK.Constants.ConnectorProperties.ConnectorName, "NGenius")
        };

        foreach (var (name, value) in pairs)
        {
            if (!string.IsNullOrEmpty(value))
                list.Add(new PSDK.PaymentProperty(PSDK.Constants.GenericNamespace.TransactionData, name, value));
        }

        return PSDK.PaymentProperty.ConvertPropertyArrayToXML(list.ToArray());
    }

    private static string TryReadPropertyFromXml(string paymentPropertiesXml, string keyName)
    {
        if (string.IsNullOrEmpty(paymentPropertiesXml)) return null;

        try
        {
            var props = PSDK.PaymentProperty.ConvertXMLToPropertyArray(paymentPropertiesXml);
            var hit = props?.FirstOrDefault(p =>
                       p.Namespace == PSDK.Constants.GenericNamespace.TransactionData &&
                       p.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase));
            return hit?.StringValue ?? hit?.StoredStringValue;
        }
        catch
        {
            return null;
        }
    }

    private static string TryReadFromExtension(ExtensionTransaction ext, string key)
    {
        if (ext?.ExtensionProperties == null) return null;
        var kv = ext.ExtensionProperties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return kv.Value?.StringValue;
    }

    private static IEnumerable<string> GetReceiptLines(JObject result)
    {
        var lines = result?["custReceipt"] as JArray ?? result?["merchReceipt"] as JArray;
        if (lines == null)
            yield break;

        foreach (var line in lines)
        {
            var text = line?["text"]?.Value<string>();
            if (!string.IsNullOrEmpty(text) && text.Contains("Card Number"))
            {
                var last4 = GetLast4Digits(text);
                yield return $"Card Number: ****{last4}";
            }
            else
            {
                yield return line.ToString(Newtonsoft.Json.Formatting.None);
            }
        }
    }

    private static string GetLast4Digits(string cardText)
    {
        var digits = new string(cardText.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits.Substring(digits.Length - 4) : digits;
    }

    public async Task<JObject> GetXReportAsync()
    {
        await EnsureClientAsync();
        var payload = new JObject
        {
            ["type"] = "getReport",
            ["reportType"] = "X"
        };
        ngClient.StartTransaction(payload);
        return await ngClient.PollUntilCompleteAsync("XReport", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
    }

    public async Task<JObject> GetZReportAsync()
    {
        await EnsureClientAsync();
        var payload = new JObject
        {
            ["type"] = "getReport",
            ["reportType"] = "Z"
        };
        ngClient.StartTransaction(payload);
        return await ngClient.PollUntilCompleteAsync("ZReport", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
    }
}
