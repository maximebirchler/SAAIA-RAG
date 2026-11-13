using System.Net.Sockets;
using System.Security.Authentication;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static void AnnotateTransportFailure(
        BackendDiagnostics.Operation probe, Exception error, string phase)
    {
        // Classification only: messages and endpoint details can contain private data.
        // Awaiting headers does not prove that the provider has not processed the request.
        probe.Set("transport_phase", phase);
        probe.Set("transport_exception_type", error.GetType().Name);
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            if (current is HttpRequestException requestError)
                probe.Set("transport_http_error", requestError.HttpRequestError.ToString());
            if (current is SocketException socketError)
            {
                probe.Set("transport_socket_error", socketError.SocketErrorCode.ToString());
                probe.Set("transport_native_error", socketError.NativeErrorCode);
            }
            if (current is AuthenticationException)
                probe.Set("transport_tls_failure", true);
        }
    }
}
