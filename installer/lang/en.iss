; Relic installer — English (default) custom messages. #included by relic.iss.
;
; THIS FILE MUST STAY UTF-8 WITH BOM (same rule as relic.iss): Inno reads a BOM-less file as
; system-codepage ANSI and every non-ASCII character (the em dash below) becomes mojibake.
;
; The names are deliberately NOT language-prefixed: an unprefixed [CustomMessages] entry is the
; default for every language in [Languages], so a language added later that lacks a translation
; for some message falls back to the English text instead of failing the compile. Translations
; override them with a prefixed copy of the same name — see lang\ro.iss.
;
; %n is a line break; %1/%2 are FmtMessage arguments (see [Code] in relic.iss for which message
; takes which). Keep the set of names identical across every lang\*.iss.

[CustomMessages]
TaskDesktopIcon=Create a desktop shortcut
TaskGroupShortcuts=Shortcuts:
StatusInstallingWebView2=Installing the WebView2 component...
RunLaunchRelic=Launch Relic
UninstCaption=Relic — uninstall
UninstIntro=Relic automatically removes all of its own data: settings, isolated profiles, logs,%nshortcuts and the Windows autostart entry. Tick what else you want removed:
UninstGamesWithCount=Also delete the downloaded game versions (%1, %2)
UninstGames=Also delete the downloaded game versions
UninstGamesHint=The game files are deleted for good; a reinstall downloads them again.
UninstGamesNone=No installed game versions were found on this computer.
UninstFiddler=Also uninstall Fiddler, together with its security certificate
UninstFiddlerHint=Recommended. Removes from the trusted list the certificate that allowed reading%nHTTPS traffic. Untick only if you also use Fiddler for something else.
UninstFiddlerNone=Fiddler is not installed.
UninstBtnCancel=Cancel
UninstBtnOk=Uninstall
UninstCleanupNotRun=Could not run the Relic cleanup. The basic settings are removed anyway, but please check the %LOCALAPPDATA%\Relic folder manually.
UninstLeftovers=The uninstall finished, but a few things could not be removed (usually because a Genshin client was still running).%n%nDetails in: %1
