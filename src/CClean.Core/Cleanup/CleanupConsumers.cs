using System.Diagnostics;
using CClean.Core.Sessions;

namespace CClean.Core.Cleanup;

public static class CleanupConsumers
{
    public static ActivityScope[] Observe(IReadOnlyList<RuleScope> rules)
    {
        var active = false; var known = true;
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (new[] { "node", "npm", "npx" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) active = true; }
                    catch (InvalidOperationException) { known = false; }
                    catch (System.ComponentModel.Win32Exception) { known = false; }
                }
            }
        }
        catch (System.ComponentModel.Win32Exception) { known = false; }
        return rules.Select(r => new ActivityScope(r.Root, active, known,
            !known ? "消费者枚举不完整" : active ? "存在 npm/node 消费者，保守保护所有 npm 根" : "未发现 npm/node 消费者；仍受会话和目录观察约束")).ToArray();
    }
}
