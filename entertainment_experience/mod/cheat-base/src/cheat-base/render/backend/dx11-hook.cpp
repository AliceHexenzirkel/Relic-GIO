#include <pch.h>
#include "dx11-hook.h"
#include <cheat-base/relic-diag.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/relic-hookwatch.h>
#include <dxgi1_2.h>

#include <cstdio>

#include <cheat-base/HookManager.h>
#pragma comment(lib, "D3dcompiler.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "winmm.lib")

// D3X HOOK DEFINITIONS
typedef HRESULT(__stdcall* IDXGISwapChainPresent)(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags);
typedef HRESULT(__stdcall* IDXGISwapChainPresent1)(IDXGISwapChain1* pSwapChain, UINT SyncInterval, UINT Flags,
	const DXGI_PRESENT_PARAMETERS* pPresentParameters);

// Definition of WndProc Hook. Its here to avoid dragging dependencies on <windows.h> types.
extern LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

static IDXGISwapChainPresent fnIDXGISwapChainPresent;
static IDXGISwapChainPresent1 fnIDXGISwapChainPresent1;
static ID3D11Device* pDevice = nullptr;

// Relic: a flip-model swap chain is presented through Present1, and dxgi routes Present into Present1
// internally, so both detours can fire for one frame. The overlay is drawn by the outermost one only.
static thread_local int s_presentDepth = 0;

// Relic: the code patch in dxgi.dll does not survive on this game - something puts the original bytes back
// within a frame, so an overlay hung on that patch flickers with every repair (and, with none, vanishes after the
// very first frame). The swap chain object carries its own vtable pointer in its first 8 bytes, so we give it
// a private copy of that vtable with our Present in slot 8. That copy lives in our DLL's data: no code in
// dxgi.dll is touched, so there is nothing left to restore and nothing left to fight over.
static void* s_vtableCopy[40];
static IDXGISwapChainPresent s_vtablePresent = nullptr;
static bool s_vtableHooked = false;

static HRESULT __stdcall Present_VTable_Hook(IDXGISwapChain* pChain, UINT SyncInterval, UINT Flags);

static void InstallVTableHook(IDXGISwapChain* pChain)
{
	if (s_vtableHooked)
		return;

	void** vtable = *reinterpret_cast<void***>(pChain);
	if (vtable == nullptr || vtable == s_vtableCopy)
		return;

	// A vtable can sit at the end of a page; copying past its last entry would fault, so the copy is guarded.
	if (!relic::Try([&]() { memcpy(s_vtableCopy, vtable, sizeof(s_vtableCopy)); }))
	{
		LOG_WARNING("Could not copy the swap chain vtable - keeping the code hook.");
		return;
	}

	s_vtablePresent = reinterpret_cast<IDXGISwapChainPresent>(vtable[8]);
	s_vtableCopy[8] = reinterpret_cast<void*>(&Present_VTable_Hook);

	DWORD previous = 0;
	if (!VirtualProtect(pChain, sizeof(void*), PAGE_READWRITE, &previous))
		return;
	*reinterpret_cast<void***>(pChain) = s_vtableCopy;
	VirtualProtect(pChain, sizeof(void*), previous, &previous);

	s_vtableHooked = true;
	LOG_DEBUG("Overlay moved onto the swap chain's own vtable; the dxgi code patch is no longer needed.");
	relic::diag::set_note("overlay on the swap chain vtable");

	// Stop rewriting dxgi's bytes: every repair only bought a frame or two and cost the ones in between.
	relic::hookwatch::stop();
}

static void DrawOverlay(IDXGISwapChain* pChain)
{
	static BOOL g_bInitialised = false;

	// Main D3D11 Objects
	static ID3D11DeviceContext* pContext = nullptr;

	if (!g_bInitialised)
	{
		auto result = (HRESULT)pChain->GetDevice(__uuidof(pDevice), reinterpret_cast<void**>(&pDevice));

		if (SUCCEEDED(result))
		{
			pDevice->GetImmediateContext(&pContext);

			DXGI_SWAP_CHAIN_DESC sd;
			pChain->GetDesc(&sd);

			backend::DX11Events::InitializeEvent(sd.OutputWindow, pDevice, pContext, pChain);

			g_bInitialised = true;
		}
	}

	// render function
	if (g_bInitialised)
	{
		InstallVTableHook(pChain);
		backend::DX11Events::RenderEvent(pContext);
	}
}

// Counts this thread's nesting for the whole call, so the decrement cannot be skipped by an exit that is
// not a plain return - a stranded counter would never reach 1 again and the overlay would be gone for the
// rest of the session, with the game still rendering normally.
struct PresentDepth
{
	PresentDepth() { s_presentDepth++; }
	~PresentDepth() { s_presentDepth--; }
	bool outermost() const { return s_presentDepth == 1; }
};

static HRESULT __stdcall Present_VTable_Hook(IDXGISwapChain* pChain, UINT SyncInterval, UINT Flags)
{
	RELIC_DIAG_TICK(PRESENT_VT);
	// The depth is held across the chained call, not released before it: the address below is still the
	// Detours-patched dxgi Present while the code patch is in place, so releasing first would let Present_Hook draw
	// the overlay a second time for every presented frame.
	PresentDepth depth;
	if (depth.outermost())
		DrawOverlay(pChain);

	// Straight to the address the game's vtable held: if the code patch is still in place this goes through
	// it, and if it has been restored this is simply dxgi's own Present.
	return s_vtablePresent(pChain, SyncInterval, Flags);
}

static HRESULT __stdcall Present_Hook(IDXGISwapChain* pChain, const UINT SyncInterval, const UINT Flags)
{
	RELIC_DIAG_TICK(PRESENT);
	PresentDepth depth;
	if (depth.outermost())
		DrawOverlay(pChain);

	return CALL_ORIGIN(Present_Hook, pChain, SyncInterval, Flags);
}

static HRESULT __stdcall Present1_Hook(IDXGISwapChain1* pChain, const UINT SyncInterval, const UINT Flags,
	const DXGI_PRESENT_PARAMETERS* pPresentParameters)
{
	RELIC_DIAG_TICK(PRESENT1);
	PresentDepth depth;
	if (depth.outermost())
		DrawOverlay(pChain);

	return CALL_ORIGIN(Present1_Hook, pChain, SyncInterval, Flags, pPresentParameters);
}

static IDXGISwapChainPresent findDirect11Present()
{
	WNDCLASSEX wc{ 0 };
	wc.cbSize = sizeof(wc);
	wc.lpfnWndProc = DefWindowProc;
	wc.lpszClassName = TEXT("Class");

	if (!RegisterClassEx(&wc))
	{
		return nullptr;
	}

	HWND hWnd = CreateWindow(wc.lpszClassName, TEXT(""), WS_DISABLED, 0, 0, 0, 0, NULL, NULL, NULL, nullptr);

	IDXGISwapChain* pSwapChain;

	D3D_FEATURE_LEVEL featureLevel;
	DXGI_SWAP_CHAIN_DESC swapChainDesc;
	ZeroMemory(&swapChainDesc, sizeof(swapChainDesc));
	swapChainDesc.BufferCount = 1;
	swapChainDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	swapChainDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
	swapChainDesc.OutputWindow = hWnd;
	swapChainDesc.SampleDesc.Count = 1;
	swapChainDesc.Windowed = TRUE;  //((GetWindowLong(hWnd, GWL_STYLE) & WS_POPUP) != 0) ? FALSE : TRUE;
	swapChainDesc.BufferDesc.ScanlineOrdering = DXGI_MODE_SCANLINE_ORDER_UNSPECIFIED;
	swapChainDesc.BufferDesc.Scaling = DXGI_MODE_SCALING_UNSPECIFIED;
	swapChainDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

	// Main D3D11 Objects
	ID3D11DeviceContext* pContext = nullptr;
	ID3D11Device* pDevice = nullptr;

	if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP,     nullptr, 0, nullptr, 1, D3D11_SDK_VERSION, 
			&swapChainDesc, &pSwapChain, &pDevice, &featureLevel, &pContext)) &&
		FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, 
			&swapChainDesc, &pSwapChain, &pDevice, &featureLevel, &pContext)))
	{
		DestroyWindow(swapChainDesc.OutputWindow);
		UnregisterClass(wc.lpszClassName, GetModuleHandle(nullptr));

		return nullptr;
	}

	const DWORD_PTR* pSwapChainVtable = reinterpret_cast<DWORD_PTR*>(pSwapChain);
	pSwapChainVtable = reinterpret_cast<DWORD_PTR*>(pSwapChainVtable[0]);

	auto swapChainPresent = reinterpret_cast<IDXGISwapChainPresent>(pSwapChainVtable[8]);

	// Present1 lives at index 22 of IDXGISwapChain1, which every swap chain since Windows 8 implements.
	IDXGISwapChain1* pSwapChain1 = nullptr;
	if (SUCCEEDED(pSwapChain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&pSwapChain1))))
	{
		const DWORD_PTR* vtable1 = *reinterpret_cast<DWORD_PTR**>(pSwapChain1);
		fnIDXGISwapChainPresent1 = reinterpret_cast<IDXGISwapChainPresent1>(vtable1[22]);
		pSwapChain1->Release();
	}

	pDevice->Release();
	//pContext->Release();
	pSwapChain->Release();

	DestroyWindow(swapChainDesc.OutputWindow);
	UnregisterClass(wc.lpszClassName, GetModuleHandle(nullptr));

	return swapChainPresent;
}

void backend::InitializeDX11Hooks() 
{
	LOG_DEBUG("Initializing D3D11 hook: started.");
	fnIDXGISwapChainPresent = findDirect11Present();
	if (fnIDXGISwapChainPresent == nullptr)
	{
		LOG_ERROR("Failed to find 'Present' function for D3D11.");
		return;
	}
	LOG_DEBUG("SwapChain Present: %p", fnIDXGISwapChainPresent);

	HookManager::install(fnIDXGISwapChainPresent, Present_Hook);
	// The patch is watched from here on: on this game something restores dxgi's original bytes right after the
	// first frame, which would otherwise make the overlay disappear.
	relic::hookwatch::protect(reinterpret_cast<void*>(fnIDXGISwapChainPresent), "Present");

	if (fnIDXGISwapChainPresent1 != nullptr)
	{
		LOG_DEBUG("SwapChain Present1: %p", fnIDXGISwapChainPresent1);
		HookManager::install(fnIDXGISwapChainPresent1, Present1_Hook);
		relic::hookwatch::protect(reinterpret_cast<void*>(fnIDXGISwapChainPresent1), "Present1");
	}
	LOG_DEBUG("Initializing D3D11 hook: done.");
}

bool backend::LoadTextureFromMemory(LPBYTE image_data, int image_width, int image_height, ID3D11ShaderResourceView** out_srv, int* out_width, int* out_height)
{
	if (pDevice == nullptr)
		return false;

	// Create texture
	D3D11_TEXTURE2D_DESC desc;
	ZeroMemory(&desc, sizeof(desc));
	desc.Width = image_width;
	desc.Height = image_height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	desc.CPUAccessFlags = 0;

	ID3D11Texture2D* pTexture = NULL;
	D3D11_SUBRESOURCE_DATA subResource;
	subResource.pSysMem = image_data;
	subResource.SysMemPitch = desc.Width * 4;
	subResource.SysMemSlicePitch = 0;
	pDevice->CreateTexture2D(&desc, &subResource, &pTexture);

	// Create texture view
	D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc;
	ZeroMemory(&srvDesc, sizeof(srvDesc));
	srvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
	srvDesc.Texture2D.MipLevels = desc.MipLevels;
	srvDesc.Texture2D.MostDetailedMip = 0;
	pDevice->CreateShaderResourceView(pTexture, &srvDesc, out_srv);
	pTexture->Release();

	*out_width = image_width;
	*out_height = image_height;

	return true;
}