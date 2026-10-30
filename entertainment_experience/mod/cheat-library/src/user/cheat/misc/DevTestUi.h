#pragma once

#if RELIC_DEVTEST
#include <cstdint>
#include <string>

#include <nlohmann/json.hpp>

// The game's UI for the dev-only test bed (DevTest.cpp): reading the UI tree, invoking a button, the game's UI
// switch, the SDK login form and the door page, and the engine statics the test bed needs. GAME THREAD ONLY: every
// call goes into il2cpp. Classes, methods and fields are resolved BY NAME through the il2cpp API, once per session,
// so nothing here depends on an address of one game build; a name a build does not have is answered as such.
namespace cheat::feature::devtest::ui
{
	// Each fills `answer` (ok, what was found, why not) for the command of that name and throws
	// std::invalid_argument for an argument of a wrong type.
	void Tree(const nlohmann::json& cmd, nlohmann::json& answer);
	void Click(const nlohmann::json& cmd, nlohmann::json& answer);
	// ui.text: the text of the InputField at a path (the new-player name, a search box).
	void SetText(const nlohmann::json& cmd, nlohmann::json& answer);

	// The game's UI (/UICamera) on or off. The camera this switched off is held (and shown again by name of that
	// object, as GameObject.Find does not see an inactive object); RestoreHud puts it back if it still exists.
	void Hud(bool visible, nlohmann::json& answer);
	bool HudHidden();
	void RestoreHud();

	// login: the account (and the password, when one is given) into the SDK's login form, then its own submit.
	// LoginFormOpen: 1 the form is shown, 0 it is not, -1 nothing can tell.
	void LoginStart(const std::string& account, const std::string* password, nlohmann::json& answer);
	int LoginFormOpen();

	// enter: the door page's own button ("bg" = bgBtn, "start" = pressStartBtn, "" = the first one that can be pressed).
	void EnterStart(const std::string& which, nlohmann::json& answer);

	// UnityEngine.Application, by name: false when the method is not found or the call faulted.
	bool RunInBackground(bool& value);

	// UnityEngine.Screen: {fullScreen (by name), width, height} of the game's window, null for what this build does
	// not have or what faulted.
	nlohmann::json ScreenState();
	bool CanQuit(std::string& error);
	bool Quit(std::string& error);

	// The team: the children of /EntityRoot/AvatarRoot, by name and active flag.
	nlohmann::json Team();

	// Is this page shown: a name is a child of /Canvas/Pages, a path starting with '/' is taken as it is.
	// -1 when the lookup faulted.
	int PageActive(const std::string& page);

	// What the by-name layer found in this build (for ping).
	nlohmann::json Probe();
}
#endif
