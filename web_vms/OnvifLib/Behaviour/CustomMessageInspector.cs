using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using System.ServiceModel;

namespace OnvifLib
{
  public class CustomMessageInspector : IClientMessageInspector
  {
    private const string WsseNamespace = @"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public List<MessageHeader> Headers { get; } = new List<MessageHeader>();

    public void AfterReceiveReply(ref Message reply, object correlationState)
    {
      foreach (var header in reply.Headers)
        if (header.Name.IndexOf("Security", StringComparison.InvariantCulture) != -1 && header.Namespace.Contains(WsseNamespace))
          reply.Headers.UnderstoodHeaders.Add(header);

      var buffer = reply.CreateBufferedCopy(int.MaxValue);
      reply = buffer.CreateMessage();
      Console.WriteLine("[ONVIF <] " + buffer.CreateMessage());
    }

    public object BeforeSendRequest(ref Message request, IClientChannel channel)
    {
      if (Headers == null)
        return request;

      foreach (var header in Headers)
        request.Headers.Insert(0, header);

      var buffer = request.CreateBufferedCopy(int.MaxValue);
      request = buffer.CreateMessage();
      Console.WriteLine("[ONVIF >] " + buffer.CreateMessage());

      return request;
    }
  }
}
