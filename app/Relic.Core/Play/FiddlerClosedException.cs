using Relic.Core.Util;

namespace Relic.Core.Play;

/// <summary>
/// The session ended because Fiddler was closed during the game: without the proxy the client no
/// longer reaches the private server (it hangs on network errors), so Relic closed the game too.
/// A dedicated type, not a generic message: both hosts (the app and the --play shortcut) must be
/// able to tell this case apart from a launch error, so they show the explanatory window instead
/// of a plain "failed".
/// </summary>
public sealed class FiddlerClosedException : InvalidOperationException
{
    public FiddlerClosedException() : base(L.T("core.play.fiddlerClosed"))
    { }
}
