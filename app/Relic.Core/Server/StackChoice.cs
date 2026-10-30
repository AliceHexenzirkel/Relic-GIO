namespace Relic.Core.Server;

/// <summary>What an agent install does with one server version: <see cref="Later"/> = the version is not set
/// up by this install (the Server page adds it later); <see cref="Have"/> = its extracted server package is in
/// the folder given; <see cref="Download"/> = the agent downloads the ready-made package into the folder given
/// once it runs; <see cref="Keep"/> = the version keeps the folder the target already uses for it — a folder
/// changes only through a relocation.</summary>
public enum StackChoice { Later, Have, Download, Keep }

/// <summary>What stands at a stack folder — the agent's <c>relocate_target_kind</c>: nothing at that name, an
/// empty folder, a folder that holds a server stack (a <c>docker-compose.yml.tmpl</c> or
/// <c>docker-compose.yml</c>), anything else (a folder with other files, a file, a folder that cannot be
/// listed). <see cref="Unknown"/> = not looked at (a network path, which could block for the network's
/// timeout).</summary>
public enum FolderKind { Absent, Empty, Stack, Other, Unknown }

/// <summary>The choice tokens: the value of the install form's <c>stack&lt;nn&gt;</c> field and of the
/// installer-only key <c>GIO_STACK_&lt;nn&gt;</c> (<see cref="ServerVersions.FormStack"/>,
/// <see cref="ServerVersions.StackKey"/>).</summary>
public static class StackChoices
{
    /// <summary>"later" | "have" | "download" | "keep" — the token of the form's stack&lt;nn&gt; and of GIO_STACK_&lt;nn&gt;.</summary>
    public static string Token(StackChoice c) => c switch
    {
        StackChoice.Have => "have",
        StackChoice.Download => "download",
        StackChoice.Keep => "keep",
        _ => "later",
    };

    /// <summary>A token, trimmed, any letter case; false for "", null and anything else (a number included —
    /// the enum's own parser would take one).</summary>
    public static bool TryParse(string? s, out StackChoice c)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "later": c = StackChoice.Later; return true;
            case "have": c = StackChoice.Have; return true;
            case "download": c = StackChoice.Download; return true;
            case "keep": c = StackChoice.Keep; return true;
            default: c = StackChoice.Later; return false;
        }
    }
}

/// <summary>A version whose folder the target's stored configuration names (AgentInstallPlan.KeepStoredFolders):
/// <paramref name="Given"/> = the folder the form named, "" when it named none; <paramref name="Replaced"/> = the form
/// named another folder, which is not used.</summary>
public sealed record StoredFolder(string Version, string Stored, string Given, bool Replaced);
