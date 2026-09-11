using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildRagEvidenceSelectionQuery(string query)
    {
        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            var focused = TryBuildComparativeFocusQuery(query);
            if (!string.IsNullOrWhiteSpace(focused))
                return focused!;
        }

        return query;
    }

}
