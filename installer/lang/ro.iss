; Relic installer — Romanian custom messages (the original wizard texts, kept for reference and
; for anyone who wants a Romanian build).
;
; NOT INCLUDED BY DEFAULT. relic.iss ships English only (lang\en.iss). To enable Romanian:
;   1. add to [Languages] in relic.iss:
;        Name: "ro"; MessagesFile: "compiler:Languages\Romanian.isl"
;   2. add, after the existing #include "lang\en.iss":
;        #include "lang\ro.iss"
; Order matters: the "ro." prefix below requires the "ro" language to exist — including this file
; without step 1 fails the compile with: Unknown language name "ro".
;
; THIS FILE MUST STAY UTF-8 WITH BOM (same rule as relic.iss): Inno reads a BOM-less file as
; system-codepage ANSI and every Romanian diacritic below becomes mojibake in the real wizard.
;
; Same names as lang\en.iss — keep the two sets identical. %n is a line break; %1/%2 are
; FmtMessage arguments.

[CustomMessages]
ro.TaskDesktopIcon=Creează un shortcut pe desktop
ro.TaskGroupShortcuts=Shortcut-uri:
ro.StatusInstallingWebView2=Se instalează componenta WebView2...
ro.RunLaunchRelic=Pornește Relic
ro.UninstCaption=Relic — dezinstalare
ro.UninstIntro=Relic își șterge automat toate datele proprii: setările, profilurile izolate, jurnalele,%nshortcut-urile și pornirea automată cu Windows. Bifează ce vrei să se șteargă în plus:
ro.UninstGamesWithCount=Șterge și versiunile de joc descărcate (%1, %2)
ro.UninstGames=Șterge și versiunile de joc descărcate
ro.UninstGamesHint=Fișierele jocului se șterg definitiv; o reinstalare le descarcă din nou.
ro.UninstGamesNone=Nu am găsit versiuni instalate pe acest calculator.
ro.UninstFiddler=Dezinstalează și Fiddler, împreună cu certificatul lui de securitate
ro.UninstFiddlerHint=Recomandat. Scoate din lista de încredere certificatul care permitea citirea%ntraficului HTTPS. Debifează doar dacă folosești Fiddler și pentru altceva.
ro.UninstFiddlerNone=Fiddler nu este instalat.
ro.UninstBtnCancel=Renunță
ro.UninstBtnOk=Dezinstalează
ro.UninstCleanupNotRun=Nu am putut rula curățarea Relic. Configurările de bază se șterg oricum, dar verifică manual folderul %LOCALAPPDATA%\Relic.
ro.UninstLeftovers=Dezinstalarea s-a terminat, dar câteva lucruri nu au putut fi șterse (de obicei pentru că un client Genshin încă rula).%n%nDetalii în: %1
