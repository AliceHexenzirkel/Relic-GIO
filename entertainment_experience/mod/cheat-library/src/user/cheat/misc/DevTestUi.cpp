#include "pch-il2cpp.h"
#include "DevTestUi.h"

#if RELIC_DEVTEST

#include "DevTestChannel.h"

#include <helpers.h>
#include <cheat-base/relic-guard.h>

#include <algorithm>
#include <cstring>
#include <vector>

// Everything here runs on the game thread, every il2cpp call under relic::Try. The UGUI classes, the SDK's login
// form and the door page are named classes in every build, so they are looked up by namespace and name through
// the il2cpp API, never by an address: a method found by name is called through its own method pointer with the
// calling convention of the build - instance methods take the object first on every version, static methods take
// a leading dummy `this` on 1.6 (il2cpp-static-thunks.h). A class or member a build lacks is answered by name.
namespace cheat::feature::devtest::ui
{
	namespace
	{
		constexpr size_t OBJECT_CACHED_PTR = 0x10;   // UnityEngine.Object.m_CachedPtr: the native object, null once destroyed

		Il2CppClass* FindClass(const char* ns, const char* name)
		{
#if RELIC_GAME_VERSION == 32
			// The 3.2 API table has no il2cpp_domain_get_assemblies: the assemblies are opened by name - the ones the
			// classes of this file live in, among the assembly names the 3.2 UserAssembly.dll carries.
			static const char* const kAssemblies[] = { "UnityEngine.CoreModule", "UnityEngine.UI", "Assembly-CSharp",
				"MiHoYoSDK", "Assembly-CSharp-firstpass", "UnityEngine.UIModule", "UnityEngine" };
			Il2CppDomain* domain = il2cpp_domain_get();
			for (const char* assemblyName : kAssemblies)
			{
				const Il2CppAssembly* assembly = domain != nullptr ? il2cpp_domain_assembly_open(domain, assemblyName) : nullptr;
				const Il2CppImage* image = assembly != nullptr ? il2cpp_assembly_get_image(assembly) : nullptr;
				if (image == nullptr)
					continue;
				Il2CppClass* klass = il2cpp_class_from_name(image, ns, name);
				if (klass != nullptr)
					return klass;
			}
			return nullptr;
#else
			size_t count = 0;
			const Il2CppAssembly** assemblies = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &count);
			for (size_t i = 0; assemblies != nullptr && i < count; i++)
			{
				const Il2CppImage* image = il2cpp_assembly_get_image(assemblies[i]);
				if (image == nullptr)
					continue;
				Il2CppClass* klass = il2cpp_class_from_name(image, ns, name);
				if (klass != nullptr)
					return klass;
			}
			return nullptr;
#endif
		}

		const MethodInfo* MethodNamed(Il2CppClass* klass, const char* name, int args)
		{
#if RELIC_GAME_VERSION == 32
			// No il2cpp_class_get_method_from_name in the 3.2 API table: the methods are walked the way it walks them,
			// the class's own first, then each parent's.
			for (Il2CppClass* k = klass; k != nullptr; k = il2cpp_class_get_parent(k))
			{
				void* iter = nullptr;
				while (const MethodInfo* m = il2cpp_class_get_methods(k, &iter))
				{
					const char* n = il2cpp_method_get_name(m);
					if (n != nullptr && strcmp(n, name) == 0 && (args < 0 || il2cpp_method_get_param_count(m) == static_cast<uint32_t>(args)))
						return m;
				}
			}
			return nullptr;
#else
			return klass != nullptr ? il2cpp_class_get_method_from_name(klass, name, args) : nullptr;
#endif
		}

		// The method `name` of `klass` itself whose one parameter is of the class `paramClass` - overloads of the
		// same arity (GetComponent(Type) / GetComponent(string)) told apart.
		const MethodInfo* MethodWithParam(Il2CppClass* klass, const char* name, const char* paramClass)
		{
			if (klass == nullptr)
				return nullptr;
			void* iter = nullptr;
#if RELIC_GAME_VERSION == 32
			// No il2cpp_method_get_param in the 3.2 API table, so the parameter's class cannot be read: a method is
			// taken only when it is the one method of that name with one parameter. GetComponent(Type) is not (it
			// shares the shape with GetComponent(string)) and stays unresolved - Component() then asks by name.
			const MethodInfo* only = nullptr;
			while (const MethodInfo* m = il2cpp_class_get_methods(klass, &iter))
			{
				const char* n = il2cpp_method_get_name(m);
				if (n == nullptr || strcmp(n, name) != 0 || il2cpp_method_get_param_count(m) != 1)
					continue;
				if (only != nullptr)
					return nullptr;
				only = m;
			}
			(void)paramClass;
			return only;
#else
			while (const MethodInfo* m = il2cpp_class_get_methods(klass, &iter))
			{
				const char* n = il2cpp_method_get_name(m);
				if (n == nullptr || strcmp(n, name) != 0 || il2cpp_method_get_param_count(m) != 1)
					continue;
				const Il2CppType* type = il2cpp_method_get_param(m, 0);
				Il2CppClass* pc = type != nullptr ? il2cpp_class_from_type(type) : nullptr;
				const char* pn = pc != nullptr ? il2cpp_class_get_name(pc) : nullptr;
				if (pn != nullptr && strcmp(pn, paramClass) == 0)
					return m;
			}
			return nullptr;
#endif
		}

		bool FieldOffset(Il2CppClass* klass, const char* name, size_t& offset)
		{
			FieldInfo* field = klass != nullptr ? il2cpp_class_get_field_from_name(klass, name) : nullptr;
			if (field == nullptr)
				return false;
			offset = il2cpp_field_get_offset(field);
			return offset != 0;
		}

		Il2CppObject* ReadObject(void* obj, size_t offset)
		{
			return *reinterpret_cast<Il2CppObject**>(reinterpret_cast<uint8_t*>(obj) + offset);
		}

		Il2CppObject* StaticObject(Il2CppClass* klass, const char* name)
		{
			FieldInfo* field = klass != nullptr ? il2cpp_class_get_field_from_name(klass, name) : nullptr;
			if (field == nullptr)
				return nullptr;
#if RELIC_GAME_VERSION == 32
			// Neither il2cpp_runtime_class_init nor il2cpp_field_static_get_value is in the 3.2 API table. A static
			// the game has set lives in the static data of the class that declares it (the field found by name may be
			// a parent's, such as a singleton base's), which exists once that class is in use; a class nobody used yet
			// has none, and then nothing is set. A thread static has no place there (offset -1).
			constexpr int FIELD_ATTRIBUTE_STATIC = 0x10;
			if ((il2cpp_field_get_flags(field) & FIELD_ATTRIBUTE_STATIC) == 0)
				return nullptr;
			size_t offset = il2cpp_field_get_offset(field);
			Il2CppClass* owner = il2cpp_field_get_parent(field);
			uint8_t* data = owner != nullptr ? static_cast<uint8_t*>(il2cpp_class_get_static_field_data(owner)) : nullptr;
			if (data == nullptr || offset == static_cast<size_t>(-1))
				return nullptr;
			return *reinterpret_cast<Il2CppObject**>(data + offset);
#else
			il2cpp_runtime_class_init(klass);   // a class nobody used yet has no static storage
			Il2CppObject* value = nullptr;
			il2cpp_field_static_get_value(field, &value);
			return value;
#endif
		}

		Il2CppObject* TypeObject(Il2CppClass* klass)
		{
#if RELIC_GAME_VERSION == 32
			// No il2cpp_class_get_type in the 3.2 API table. The class's own type is its byval_arg (Il2CppClass of the
			// 3.2 types); it is used only once the API confirms it - the class that type names is this class.
			if (klass == nullptr)
				return nullptr;
			const Il2CppType* type = &klass->byval_arg;
			if (il2cpp_type_get_class_or_element_class(type) != klass)
				return nullptr;
			return il2cpp_type_get_object(type);
#else
			return klass != nullptr ? il2cpp_type_get_object(il2cpp_class_get_type(klass)) : nullptr;
#endif
		}

		bool Alive(void* unityObject)
		{
			return unityObject != nullptr && *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(unityObject) + OBJECT_CACHED_PTR) != nullptr;
		}

		template <class R, class... A>
		R CallInstance(const MethodInfo* m, void* self, A... args)
		{
			using Fn = R (*)(void*, A..., const MethodInfo*);
			return reinterpret_cast<Fn>(m->methodPointer)(self, args..., m);
		}

		template <class R, class... A>
		R CallStatic(const MethodInfo* m, A... args)
		{
#if RELIC_GAME_VERSION == 16
			using Fn = R (*)(void*, A..., const MethodInfo*);
			return reinterpret_cast<Fn>(m->methodPointer)(nullptr, args..., m);
#else
			using Fn = R (*)(A..., const MethodInfo*);
			return reinterpret_cast<Fn>(m->methodPointer)(args..., m);
#endif
		}

		// The method an object really runs for `m`: an override in its own class, else `m`.
		const MethodInfo* Virtual(void* obj, const MethodInfo* m)
		{
			const MethodInfo* v = il2cpp_object_get_virtual_method(reinterpret_cast<Il2CppObject*>(obj), m);
			return v != nullptr ? v : m;
		}

		// A managed string as UTF-8, at most `maxChars` UTF-16 units of it (never half a surrogate pair).
		std::string Text(void* s, size_t maxChars = TEXT_MAX)
		{
			if (s == nullptr)
				return std::string();
			Il2CppString* str = reinterpret_cast<Il2CppString*>(s);
			int32_t length = il2cpp_string_length(str);
			const Il2CppChar* chars = il2cpp_string_chars(str);
			if (length <= 0 || chars == nullptr)
				return std::string();
			size_t n = std::min<size_t>(static_cast<size_t>(length), maxChars);
			if (n < static_cast<size_t>(length) && n > 0 && chars[n - 1] >= 0xD800 && chars[n - 1] <= 0xDBFF)
				n--;
			const wchar_t* w = reinterpret_cast<const wchar_t*>(chars);
			int bytes = WideCharToMultiByte(CP_UTF8, 0, w, static_cast<int>(n), nullptr, 0, nullptr, nullptr);
			std::string out(bytes > 0 ? static_cast<size_t>(bytes) : 0, '\0');
			if (bytes > 0)
				WideCharToMultiByte(CP_UTF8, 0, w, static_cast<int>(n), out.data(), bytes, nullptr, nullptr);
			if (n < static_cast<size_t>(length))
				out += "...";
			return out;
		}

		// A managed string: ASCII through the marshaller every feature already uses, other text as UTF-8.
		app::String* Managed(const std::string& s)
		{
			bool ascii = std::all_of(s.begin(), s.end(), [](char c) { return static_cast<unsigned char>(c) < 0x80; });
			return ascii ? string_to_il2cppi(s) : reinterpret_cast<app::String*>(il2cpp_string_new(s.c_str()));
		}

		std::string Fault(const char* what)
		{
			return fmt::format("{} faulted (0x{:08X})", what, relic::g_lastGuardCode);
		}

		// ---- the names, resolved once per session

		struct Api
		{
			bool tried = false;
			std::string error;
			Il2CppClass* gameObject = nullptr;
			const MethodInfo* getComponent = nullptr;       // GameObject.GetComponent(Type)
			const MethodInfo* activeSelf = nullptr;         // GameObject.get_activeSelf
			const MethodInfo* activeInHierarchy = nullptr;  // GameObject.get_activeInHierarchy
			Il2CppObject* textType = nullptr;               // UnityEngine.UI.Text
			const MethodInfo* textGet = nullptr;
			Il2CppObject* simpleTextType = nullptr;         // UnityEngine.UI.SimpleText, the client's own text component
			const MethodInfo* simpleTextGet = nullptr;
			Il2CppObject* buttonType = nullptr;             // UnityEngine.UI.Button
			const MethodInfo* onClick = nullptr;            // Button.get_onClick
			const MethodInfo* onSubmit = nullptr;           // Button.OnSubmit(BaseEventData)
			const MethodInfo* interactable = nullptr;       // Selectable.IsInteractable
			const MethodInfo* invoke = nullptr;             // UnityEngine.Events.UnityEvent.Invoke
			const MethodInfo* setText = nullptr;            // UnityEngine.UI.InputField.set_text
			const MethodInfo* findObjectOfType = nullptr;   // UnityEngine.Object.FindObjectOfType(Type)
			const MethodInfo* quit = nullptr;               // UnityEngine.Application.Quit
			const MethodInfo* runInBackground = nullptr;    // UnityEngine.Application.get_runInBackground
			const MethodInfo* fullScreen = nullptr;         // UnityEngine.Screen.get_fullScreen
		};
		Api g_Api;

		Api& Resolve()
		{
			if (g_Api.tried)
				return g_Api;
			g_Api.tried = true;
			// Each name on its own: one that faults (an API entry a build lacks) leaves the others resolved, and the
			// error names every step that faulted.
			Api& a = g_Api;
			std::vector<std::string> failed;
			auto step = [&](const char* what, auto&& fn)
			{
				if (!relic::Try(std::move(fn)))
					failed.push_back(Fault(what));
			};
			Il2CppClass* text = nullptr;
			Il2CppClass* simpleText = nullptr;
			Il2CppClass* button = nullptr;
			Il2CppClass* application = nullptr;
			step("GameObject", [&]() { a.gameObject = FindClass("UnityEngine", "GameObject"); });
			step("GameObject.GetComponent", [&]() { a.getComponent = MethodWithParam(a.gameObject, "GetComponent", "Type"); });
			step("GameObject.activeSelf", [&]() { a.activeSelf = MethodNamed(a.gameObject, "get_activeSelf", 0); });
			step("GameObject.activeInHierarchy", [&]() { a.activeInHierarchy = MethodNamed(a.gameObject, "get_activeInHierarchy", 0); });
			step("Text", [&]() { text = FindClass("UnityEngine.UI", "Text"); a.textType = TypeObject(text); a.textGet = MethodNamed(text, "get_text", 0); });
			step("SimpleText", [&]() { simpleText = FindClass("UnityEngine.UI", "SimpleText"); a.simpleTextType = TypeObject(simpleText); a.simpleTextGet = MethodNamed(simpleText, "get_text", 0); });
			step("Button", [&]() { button = FindClass("UnityEngine.UI", "Button"); a.buttonType = TypeObject(button); });
			step("Button.onClick", [&]() { a.onClick = MethodNamed(button, "get_onClick", 0); a.onSubmit = MethodNamed(button, "OnSubmit", 1); });
			step("Selectable.IsInteractable", [&]() { a.interactable = MethodNamed(FindClass("UnityEngine.UI", "Selectable"), "IsInteractable", 0); });
			step("UnityEvent.Invoke", [&]() { a.invoke = MethodNamed(FindClass("UnityEngine.Events", "UnityEvent"), "Invoke", 0); });
			step("InputField.text", [&]() { a.setText = MethodNamed(FindClass("UnityEngine.UI", "InputField"), "set_text", 1); });
			step("Object.FindObjectOfType", [&]() { a.findObjectOfType = MethodWithParam(FindClass("UnityEngine", "Object"), "FindObjectOfType", "Type"); });
			step("Application", [&]() { application = FindClass("UnityEngine", "Application"); a.quit = MethodNamed(application, "Quit", 0); a.runInBackground = MethodNamed(application, "get_runInBackground", 0); });
			step("Screen.fullScreen", [&]() { a.fullScreen = MethodNamed(FindClass("UnityEngine", "Screen"), "get_fullScreen", 0); });
			for (size_t i = 0; i < failed.size(); i++)
				a.error += (i > 0 ? "; " : "") + failed[i];
			return g_Api;
		}

#if RELIC_GAME_VERSION == 32
		// The 3.2 UI assembly is not reached by name, so Button's methods are learned from the first live button that
		// is pressed: its own class and its parents (Selectable) hold get_onClick, OnSubmit and IsInteractable.
		void LearnButton(void* button)
		{
			Api& api = g_Api;
			if (button == nullptr || (api.onClick != nullptr && api.onSubmit != nullptr && api.interactable != nullptr))
				return;
			Il2CppClass* klass = il2cpp_object_get_class(reinterpret_cast<Il2CppObject*>(button));
			if (klass == nullptr)
				return;
			if (api.onClick == nullptr)
				api.onClick = MethodNamed(klass, "get_onClick", 0);
			if (api.onSubmit == nullptr)
				api.onSubmit = MethodNamed(klass, "OnSubmit", 1);
			if (api.interactable == nullptr)
				api.interactable = MethodNamed(klass, "IsInteractable", 0);
		}
#endif

		app::GameObject* Find(const std::string& path)
		{
			return app::GameObject_Find(Managed(path), nullptr);
		}

		bool ActiveSelf(app::GameObject* go)
		{
			Api& api = Resolve();
			return api.activeSelf != nullptr ? CallInstance<bool>(api.activeSelf, go) : app::GameObject_get_active(go, nullptr);
		}

		bool ActiveInHierarchy(app::GameObject* go)
		{
			Api& api = Resolve();
			return api.activeInHierarchy != nullptr ? CallInstance<bool>(api.activeInHierarchy, go) : app::GameObject_get_active(go, nullptr);
		}

		void* Component(app::GameObject* go, Il2CppObject* type, const char* name)
		{
			Api& api = Resolve();
			if (api.getComponent != nullptr && type != nullptr)
				return CallInstance<void*>(api.getComponent, go, type);
			return app::GameObject_GetComponentByName(go, string_to_il2cppi(name), nullptr);
		}

		std::string NodeText(app::GameObject* go)
		{
			Api& api = Resolve();
			if (void* text = Component(go, api.textType, "Text"))
				return Text(api.textGet != nullptr ? CallInstance<app::String*>(api.textGet, text)
					: app::Text_get_text(reinterpret_cast<app::Text*>(text), nullptr));
			if (api.simpleTextType != nullptr && api.simpleTextGet != nullptr)
				if (void* simple = Component(go, api.simpleTextType, "SimpleText"))
					return Text(CallInstance<app::String*>(api.simpleTextGet, simple));
			return std::string();
		}

		// Presses a button the way a click does: only an active one that is interactable.
		void Press(void* button, const std::string& via, nlohmann::json& answer)
		{
			Api& api = Resolve();
			if (button == nullptr || !Alive(button))
			{
				answer["ok"] = false;
				answer["reason"] = "notFound";
				answer["error"] = "no button there";
				return;
			}
#if RELIC_GAME_VERSION == 32
			LearnButton(button);
#endif
			app::GameObject* go = app::Component_1_get_gameObject(reinterpret_cast<app::Component_1*>(button), nullptr);
			if (go == nullptr || !ActiveInHierarchy(go))
			{
				answer["ok"] = false;
				answer["reason"] = "inactive";
				answer["error"] = "the button is not active";
				return;
			}
			if (api.interactable == nullptr)
			{
				answer["ok"] = false;
				answer["reason"] = "unresolved";
				answer["error"] = "Selectable.IsInteractable is not found in this build";
				return;
			}
			if (!CallInstance<bool>(Virtual(button, api.interactable), button))
			{
				answer["ok"] = false;
				answer["reason"] = "notInteractable";
				answer["error"] = "the button is not interactable";
				return;
			}
			if (via == "submit")
			{
				if (api.onSubmit == nullptr)
				{
					answer["ok"] = false;
					answer["reason"] = "unresolved";
					answer["error"] = "Button.OnSubmit is not found in this build";
					return;
				}
				CallInstance<void, void*>(Virtual(button, api.onSubmit), button, nullptr);
			}
			else
			{
				if (api.onClick == nullptr)
				{
					answer["ok"] = false;
					answer["reason"] = "unresolved";
					answer["error"] = "Button.get_onClick is not found in this build";
					return;
				}
				void* event = CallInstance<void*>(api.onClick, button);
				if (event == nullptr)
				{
					answer["ok"] = false;
					answer["reason"] = "noEvent";
					answer["error"] = "the button has no click event";
					return;
				}
#if RELIC_GAME_VERSION == 32
				if (api.invoke == nullptr)
					if (Il2CppClass* eventClass = il2cpp_object_get_class(reinterpret_cast<Il2CppObject*>(event)))
						api.invoke = MethodNamed(eventClass, "Invoke", 0);
#endif
				if (api.invoke == nullptr)
				{
					answer["ok"] = false;
					answer["reason"] = "unresolved";
					answer["error"] = "UnityEvent.Invoke is not found in this build";
					return;
				}
				CallInstance<void>(api.invoke, event);
			}
			answer["invoked"] = true;
			answer["via"] = via == "submit" ? "submit" : "onClick";
		}

		// ---- the login form and the door page, per game version

		struct LoginForm
		{
			const char* ns;
			const char* name;
			const char* instance;      // the static that holds the form
			const char* account;       // InputField
			const char* password;      // InputField
			const char* submit;        // the form's own login method, no parameters
			const char* gameObject;    // a field holding the form's GameObject; null: the form is a component itself
		};

#if RELIC_GAME_VERSION == 28
		const LoginForm kLoginForm = { "MiHoYo.SDK.Win", "LoginByAccountOversea", "Instance", "accountInput", "passwordInput", "ClickLoginButton", nullptr };
		constexpr bool HAVE_LOGIN_FORM = true;
#elif RELIC_GAME_VERSION == 16
		const LoginForm kLoginForm = { "MiHoYo.SDK", "MiHoYoSDKAccountLogin", "_instance", "accountInput", "passwordInput", "Login", "gameObject" };
		constexpr bool HAVE_LOGIN_FORM = true;
#elif RELIC_GAME_VERSION == 32
		// The 2.8 SDK's form, every part looked up by name: a 3.2 SDK without it answers that the class is not in this build.
		const LoginForm kLoginForm = { "MiHoYo.SDK.Win", "LoginByAccountOversea", "Instance", "accountInput", "passwordInput", "ClickLoginButton", nullptr };
		constexpr bool HAVE_LOGIN_FORM = true;
#else
		const LoginForm kLoginForm = { "", "", "", "", "", "", nullptr };
		constexpr bool HAVE_LOGIN_FORM = false;
#endif

		Il2CppClass* g_FormClass = nullptr;
		bool g_FormTried = false;

		Il2CppClass* FormClass()
		{
			if (!g_FormTried && HAVE_LOGIN_FORM)
			{
				g_FormTried = true;
				g_FormClass = FindClass(kLoginForm.ns, kLoginForm.name);
			}
			return g_FormClass;
		}

		// The form's GameObject, null when the form is not there.
		app::GameObject* FormObject(Il2CppObject* form)
		{
			if (form == nullptr)
				return nullptr;
			if (kLoginForm.gameObject == nullptr)
				return Alive(form) ? app::Component_1_get_gameObject(reinterpret_cast<app::Component_1*>(form), nullptr) : nullptr;
			size_t offset = 0;
			if (!FieldOffset(FormClass(), kLoginForm.gameObject, offset))
				return nullptr;
			Il2CppObject* go = ReadObject(form, offset);
			return Alive(go) ? reinterpret_cast<app::GameObject*>(go) : nullptr;
		}

		// the object held by a field of `obj`, by the field's name; found = whether the field exists
		Il2CppObject* Member(Il2CppClass* klass, void* obj, const char* name, bool& found)
		{
			size_t offset = 0;
			found = FieldOffset(klass, name, offset);
			return found ? ReadObject(obj, offset) : nullptr;
		}

		struct HudHold
		{
			app::GameObject* go = nullptr;
			uint32_t handle = 0;   // keeps the managed object alive while it is held
		};
		HudHold g_Hud;
	}

	void Tree(const nlohmann::json& cmd, nlohmann::json& answer)
	{
		std::string root = Arg<std::string>(cmd, "root", "/Canvas/Pages");
		int depth = std::clamp(Arg<int>(cmd, "depth", 6), 0, 64);
		bool activeOnly = Arg<bool>(cmd, "activeOnly", false);
		bool texts = Arg<bool>(cmd, "texts", true);
		int limit = std::clamp(Arg<int>(cmd, "limit", 2000), 1, 20000);
		answer["root"] = root;
		if (root.empty() || root[0] != '/')
		{
			answer["ok"] = false;
			answer["error"] = "root: a path from the scene's top, starting with '/'";
			return;
		}
		Resolve();
		app::GameObject* top = nullptr;
		if (!relic::Try([&]() { top = Find(root); }))
		{
			answer["ok"] = false;
			answer["error"] = Fault("GameObject.Find");
			return;
		}
		if (top == nullptr)
		{
			answer["ok"] = false;
			answer["error"] = "no active object at " + Brief(root);
			return;
		}

		struct Item { app::Transform* transform; std::string parent; int depth; };
		std::vector<Item> stack;
		app::Transform* topTransform = nullptr;
		relic::Try([&]() { topTransform = app::GameObject_get_transform(top, nullptr); });
		if (topTransform != nullptr)
			stack.push_back({ topTransform, std::string(), 0 });
		nlohmann::json nodes = nlohmann::json::array();
		int faults = 0;
		bool truncated = false;
		while (!stack.empty())
		{
			if (static_cast<int>(nodes.size()) >= limit)
			{
				truncated = true;
				break;
			}
			Item item = std::move(stack.back());
			stack.pop_back();
			std::string name, text;
			bool active = false, button = false, interactable = false;
			std::vector<app::Transform*> children;
			bool ok = relic::Try([&]()
			{
				app::GameObject* go = app::Component_1_get_gameObject(reinterpret_cast<app::Component_1*>(item.transform), nullptr);
				if (go == nullptr)
					return;
				name = Text(app::Object_1_get_name(reinterpret_cast<app::Object_1*>(go), nullptr), 120);
				active = ActiveSelf(go);
				if (activeOnly && !active)
					return;
				Api& api = g_Api;
				if (void* b = Component(go, api.buttonType, "Button"))
				{
					button = true;
					interactable = api.interactable != nullptr && CallInstance<bool>(Virtual(b, api.interactable), b);
				}
				if (texts)
					text = NodeText(go);
				if (item.depth < depth)
				{
					int count = app::Transform_get_childCount(item.transform, nullptr);
					for (int i = 0; i < count && i < 4096; i++)
						children.push_back(app::Transform_GetChild(item.transform, i, nullptr));
				}
			});
			if (!ok)
			{
				faults++;
				continue;
			}
			if (activeOnly && !active)
				continue;
			std::string path = item.parent.empty() ? root : item.parent + "/" + name;
			nlohmann::json node = { { "path", path }, { "active", active } };
			if (button)
			{
				node["button"] = true;
				node["interactable"] = interactable;
			}
			if (!text.empty())
				node["text"] = text;
			nodes.push_back(std::move(node));
			for (auto it = children.rbegin(); it != children.rend(); ++it)   // pushed backwards: visited in order
				if (*it != nullptr)
					stack.push_back({ *it, path, item.depth + 1 });
		}
		answer["nodes"] = std::move(nodes);
		answer["count"] = answer["nodes"].size();
		answer["truncated"] = truncated;
		if (faults > 0)
			answer["faults"] = faults;
	}

	void SetText(const nlohmann::json& cmd, nlohmann::json& answer)
	{
		std::string path = Arg<std::string>(cmd, "path", std::string());
		auto t = cmd.find("text");
		answer["path"] = path;
		if (path.empty() || path[0] != '/' || t == cmd.end() || !t->is_string())
		{
			answer["ok"] = false;
			answer["error"] = "path (from the scene's top, starting with '/', as ui.tree names it) and text are needed";
			return;
		}
		std::string text = t->get<std::string>();
		Api& api = Resolve();
		std::string error;
		bool ok = relic::Try([&]()
		{
			app::GameObject* go = Find(path);
			if (go == nullptr)
			{
				answer["reason"] = "notFound";
				error = "no active object at that path";
				return;
			}
			void* field = app::GameObject_GetComponentByName(go, string_to_il2cppi("InputField"), nullptr);
			if (field == nullptr)
			{
				answer["reason"] = "notAnInputField";
				error = "the object has no InputField";
				return;
			}
			// InputField.set_text as the login uses it, else learned from the field's own class (3.2 cannot reach the
			// UI assembly by name).
			const MethodInfo* setText = api.setText;
			if (setText == nullptr)
				if (Il2CppClass* klass = il2cpp_object_get_class(reinterpret_cast<Il2CppObject*>(field)))
					setText = MethodNamed(klass, "set_text", 1);
			if (setText == nullptr)
			{
				answer["reason"] = "unresolved";
				error = "InputField.set_text is not found in this build";
				return;
			}
			CallInstance<void>(setText, field, Managed(text));
			answer["set"] = true;
		});
		if (!ok)
			error = Fault("the text");
		if (!error.empty())
		{
			answer["ok"] = false;
			answer["error"] = error;
		}
	}

	void Click(const nlohmann::json& cmd, nlohmann::json& answer)
	{
		std::string path = Arg<std::string>(cmd, "path", std::string());
		std::string via = Arg<std::string>(cmd, "via", std::string("onClick"));
		answer["path"] = path;
		if (path.empty() || path[0] != '/')
		{
			answer["ok"] = false;
			answer["error"] = "path: the node's path from the scene's top, starting with '/' (as ui.tree names it)";
			return;
		}
		if (via != "onClick" && via != "submit")
		{
			answer["ok"] = false;
			answer["error"] = "via: onClick (the button's click event) or submit (Button.OnSubmit)";
			return;
		}
		Api& api = Resolve();
#if RELIC_GAME_VERSION != 32
		// 3.2 finds the component by its name and learns Button's methods from it (LearnButton).
		if (api.buttonType == nullptr)
		{
			answer["ok"] = false;
			answer["reason"] = "unresolved";
			answer["error"] = "UnityEngine.UI.Button is not found in this build" + (api.error.empty() ? std::string() : " (" + api.error + ")");
			return;
		}
#endif
		bool ok = relic::Try([&]()
		{
			app::GameObject* go = Find(path);
			if (go == nullptr)
			{
				answer["ok"] = false;
				answer["reason"] = "notFound";
				answer["error"] = "no active object at that path";
				return;
			}
			void* button = Component(go, api.buttonType, "Button");
			if (button == nullptr)
			{
				answer["ok"] = false;
				answer["reason"] = "notAButton";
				answer["error"] = "the object has no Button";
				return;
			}
			Press(button, via, answer);
		});
		if (!ok)
		{
			answer["ok"] = false;
			answer["error"] = Fault("the click");
		}
	}

	void Hud(bool visible, nlohmann::json& answer)
	{
		bool ok = relic::Try([&]()
		{
			if (!visible)
			{
				if (g_Hud.go != nullptr)
				{
					answer["was"] = false;   // hidden by an earlier command
					answer["now"] = false;
					return;
				}
				app::GameObject* go = Find("/UICamera");
				if (go == nullptr)
				{
					answer["ok"] = false;
					answer["error"] = "no UI camera here (none in this scene, or another feature hides it)";
					return;
				}
				g_Hud.handle = il2cpp_gchandle_new(reinterpret_cast<Il2CppObject*>(go), false);
				g_Hud.go = go;
				app::GameObject_SetActive(go, false, nullptr);
				answer["was"] = true;
				answer["now"] = false;
				return;
			}
			if (g_Hud.go != nullptr)
			{
				bool alive = Alive(g_Hud.go);
				if (alive)
					app::GameObject_SetActive(g_Hud.go, true, nullptr);
				il2cpp_gchandle_free(g_Hud.handle);
				g_Hud = HudHold{};
				answer["was"] = false;
				answer["now"] = alive;
				if (!alive)
					answer["note"] = "the UI camera that was hidden is gone (the scene changed)";
				return;
			}
			if (Find("/UICamera") == nullptr)
			{
				answer["ok"] = false;
				answer["error"] = "no active UI camera to show (none in this scene, or another feature hides it)";
				return;
			}
			answer["was"] = true;
			answer["now"] = true;
		});
		if (!ok)
		{
			answer["ok"] = false;
			answer["error"] = Fault("the UI switch");
		}
	}

	bool HudHidden()
	{
		return g_Hud.go != nullptr;
	}

	void RestoreHud()
	{
		if (g_Hud.go == nullptr)
			return;
		relic::Try([&]()
		{
			if (Alive(g_Hud.go))
				app::GameObject_SetActive(g_Hud.go, true, nullptr);
			il2cpp_gchandle_free(g_Hud.handle);
		});
		g_Hud = HudHold{};
	}

	void LoginStart(const std::string& account, const std::string* password, nlohmann::json& answer)
	{
		nlohmann::json found = nlohmann::json::object();
		answer["form"] = HAVE_LOGIN_FORM ? std::string(kLoginForm.ns) + "." + kLoginForm.name : std::string();
		if (!HAVE_LOGIN_FORM)
		{
			answer["ok"] = false;
			answer["error"] = "no login form is known for this game version";
			return;
		}
		Api& api = Resolve();
		std::string error;
		bool ok = relic::Try([&]()
		{
			Il2CppClass* klass = FormClass();
			found["class"] = klass != nullptr;
			if (klass == nullptr)
			{
				error = "the class is not in this build";
				return;
			}
			Il2CppObject* form = StaticObject(klass, kLoginForm.instance);
			found["instance"] = form != nullptr;
			if (form == nullptr)
			{
				error = "the login form is not open (its static is empty)";
				return;
			}
			app::GameObject* go = FormObject(form);
			bool shown = go != nullptr && ActiveInHierarchy(go);
			found["shown"] = shown;
			if (!shown)
			{
				error = "the login form is not shown";
				return;
			}
			bool accountField = false, passwordField = false;
			Il2CppObject* accountInput = Member(klass, form, kLoginForm.account, accountField);
			Il2CppObject* passwordInput = Member(klass, form, kLoginForm.password, passwordField);
			found["accountInput"] = accountInput != nullptr;
			found["passwordInput"] = passwordInput != nullptr;
			found["setText"] = api.setText != nullptr;
			const MethodInfo* submit = MethodNamed(klass, kLoginForm.submit, 0);
			found["submit"] = submit != nullptr;
			if (accountInput == nullptr || (password != nullptr && passwordInput == nullptr) || api.setText == nullptr || submit == nullptr)
			{
				error = "a part of the form is missing (see found)";
				return;
			}
			CallInstance<void>(api.setText, accountInput, Managed(account));
			if (password != nullptr)
				CallInstance<void>(api.setText, passwordInput, Managed(*password));
			CallInstance<void>(submit, form);
			answer["invoked"] = std::string(kLoginForm.submit);
		});
		answer["found"] = found;
		if (!ok)
			error = Fault("the login form");
		if (!error.empty())
		{
			answer["ok"] = false;
			answer["error"] = error;
		}
	}

	int LoginFormOpen()
	{
		if (!HAVE_LOGIN_FORM)
			return -1;
		int open = -1;
		relic::Try([&]()
		{
			Il2CppClass* klass = FormClass();
			if (klass == nullptr)
				return;
			app::GameObject* go = FormObject(StaticObject(klass, kLoginForm.instance));
			open = go != nullptr && ActiveInHierarchy(go) ? 1 : 0;
		});
		return open;
	}

	void EnterStart(const std::string& which, nlohmann::json& answer)
	{
		if (which != "" && which != "bg" && which != "start")
		{
			answer["ok"] = false;
			answer["error"] = "button: start (pressStartBtn), bg (bgBtn) or left out (the first that can be pressed)";
			return;
		}
		Api& api = Resolve();
		nlohmann::json found = nlohmann::json::object();
		std::string error;
		bool ok = relic::Try([&]()
		{
#if RELIC_GAME_VERSION == 32
			// 3.2: the door page's buttons by their place in the UI tree, their Button component by its name.
			const char* paths[2] = { "/Canvas/Pages/LoginMainPage/GrpLogin/Center/BtnStart", "/Canvas/Pages/LoginMainPage/GrpLogin/BgBtn" };
			const char* names[2] = { "BtnStart", "BgBtn" };
			const char* keys[2] = { "start", "bg" };
			found["byPath"] = true;
			bool seen = false;
			for (int i = 0; i < 2; i++)
			{
				if (!which.empty() && which != keys[i])
					continue;
				app::GameObject* go = Find(paths[i]);
				found[names[i]] = go != nullptr;
				if (go == nullptr)
					continue;
				seen = true;
				void* button = Component(go, api.buttonType, "Button");
				if (button == nullptr)
				{
					found[std::string(names[i]) + "Refused"] = "no Button component";
					continue;
				}
				nlohmann::json press = { { "ok", true } };
				Press(button, "onClick", press);
				if (press.value("ok", false))
				{
					answer["invoked"] = names[i];
					return;
				}
				found[std::string(names[i]) + "Refused"] = press.value("error", std::string());
			}
			error = seen ? "no button of the door page could be pressed (see found)" : "the door page is not open";
#else
			Il2CppClass* page = FindClass("MoleMole", "MonoLoginMainPage");
			found["class"] = page != nullptr;
			found["findObjectOfType"] = api.findObjectOfType != nullptr;
			if (page == nullptr || api.findObjectOfType == nullptr)
			{
				error = "the door page's class or Object.FindObjectOfType is not in this build";
				return;
			}
			void* instance = CallStatic<void*>(api.findObjectOfType, TypeObject(page));
			found["page"] = instance != nullptr;
			if (instance == nullptr)
			{
				error = "the door page is not open";
				return;
			}
			// pressStartBtn is the page's "click to begin": it starts the game. bgBtn is pressable as well and is not
			// the way in, so it is only the second choice.
			const char* names[2] = { "pressStartBtn", "bgBtn" };
			const char* keys[2] = { "start", "bg" };
			for (int i = 0; i < 2; i++)
			{
				if (!which.empty() && which != keys[i])
					continue;
				bool field = false;
				Il2CppObject* button = Member(page, instance, names[i], field);
				found[names[i]] = button != nullptr;
				if (button == nullptr)
					continue;
				nlohmann::json press = nlohmann::json::object();
				press["ok"] = true;
				Press(button, "onClick", press);
				if (press.value("ok", false))
				{
					answer["invoked"] = names[i];
					return;
				}
				found[std::string(names[i]) + "Refused"] = press.value("error", std::string());
			}
			error = "no button of the door page could be pressed (see found)";
#endif
		});
		answer["found"] = found;
		if (!ok)
			error = Fault("the door page");
		if (!error.empty())
		{
			answer["ok"] = false;
			answer["error"] = error;
		}
	}

	bool RunInBackground(bool& value)
	{
		Api& api = Resolve();
		if (api.runInBackground == nullptr)
			return false;
		bool result = false;
		if (!relic::Try([&]() { result = CallStatic<bool>(api.runInBackground); }))
			return false;
		value = result;
		return true;
	}

	nlohmann::json ScreenState()
	{
		Api& api = Resolve();
		nlohmann::json screen = { { "fullScreen", nullptr }, { "width", nullptr }, { "height", nullptr } };
		bool full = false;
		if (api.fullScreen != nullptr && relic::Try([&]() { full = CallStatic<bool>(api.fullScreen); }))
			screen["fullScreen"] = full;
		int32_t width = 0, height = 0;
		// the bindings every version has (the 1.6 ones through its static thunks), as the map and the ESP use them
		if (relic::Try([&]() { width = app::Screen_get_width(nullptr); height = app::Screen_get_height(nullptr); }))
		{
			screen["width"] = width;
			screen["height"] = height;
		}
		return screen;
	}

	bool CanQuit(std::string& error)
	{
		Api& api = Resolve();
		if (api.quit == nullptr)
		{
			error = "UnityEngine.Application.Quit is not found in this build" + (api.error.empty() ? std::string() : " (" + api.error + ")");
			return false;
		}
		return true;
	}

	bool Quit(std::string& error)
	{
		if (!CanQuit(error))
			return false;
		Api& api = Resolve();
		if (!relic::Try([&]() { CallStatic<void>(api.quit); }))
		{
			error = Fault("Application.Quit");
			return false;
		}
		return true;
	}

	nlohmann::json Team()
	{
		nlohmann::json team = nlohmann::json::array();
		relic::Try([&]()
		{
			app::GameObject* root = Find("/EntityRoot/AvatarRoot");
			if (root == nullptr)
				return;
			app::Transform* t = app::GameObject_get_transform(root, nullptr);
			int count = t != nullptr ? app::Transform_get_childCount(t, nullptr) : 0;
			for (int i = 0; i < count && i < 64; i++)
			{
				app::Transform* child = app::Transform_GetChild(t, i, nullptr);
				app::GameObject* go = child != nullptr ? app::Component_1_get_gameObject(reinterpret_cast<app::Component_1*>(child), nullptr) : nullptr;
				if (go == nullptr)
					continue;
				team.push_back({ { "name", Text(app::Object_1_get_name(reinterpret_cast<app::Object_1*>(go), nullptr), 120) },
					{ "active", ActiveSelf(go) } });
			}
		});
		return team;
	}

	int PageActive(const std::string& page)
	{
		std::string path = !page.empty() && page[0] == '/' ? page : "/Canvas/Pages/" + page;
		int active = -1;
		relic::Try([&]() { active = Find(path) != nullptr ? 1 : 0; });   // Find sees active objects only
		return active;
	}

	nlohmann::json Probe()
	{
		Api& api = Resolve();
		nlohmann::json p = {
			{ "getComponent", api.getComponent != nullptr },
			{ "text", api.textType != nullptr && api.textGet != nullptr },
			{ "simpleText", api.simpleTextType != nullptr && api.simpleTextGet != nullptr },
			{ "button", api.buttonType != nullptr && api.onClick != nullptr && api.invoke != nullptr },
			{ "interactable", api.interactable != nullptr },
			{ "inputField", api.setText != nullptr },
			{ "findObjectOfType", api.findObjectOfType != nullptr },
			{ "quit", api.quit != nullptr },
			{ "runInBackground", api.runInBackground != nullptr },
			{ "fullScreen", api.fullScreen != nullptr },
			{ "loginForm", HAVE_LOGIN_FORM ? std::string(kLoginForm.ns) + "." + kLoginForm.name : std::string() },
		};
		bool formFound = false;
		relic::Try([&]() { formFound = FormClass() != nullptr; });
		p["loginFormFound"] = formFound;
		if (!api.error.empty())
			p["error"] = api.error;
		return p;
	}
}
#endif
