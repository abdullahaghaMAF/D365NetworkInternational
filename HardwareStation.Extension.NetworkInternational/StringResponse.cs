using Microsoft.Dynamics.Commerce.Runtime.Messages;
public sealed class StringResponse : Response
{
    public string Value { get; }

    public StringResponse(string value)
    {
        Value = value;
    }
}