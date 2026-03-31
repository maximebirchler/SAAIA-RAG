using System.Threading.Tasks;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock
{
    private static async Task OpenAsync(string docPath, int page)
    {
        try
        {
            _ = await DocumentLauncher.TryOpenAsync(docPath, page);
        }
        catch
        {
            // silent (UX)
        }
    }
}
