#pragma once
#include <Windows.h>

#include <d3d11.h>
#include <d3d12.h>

#include <d3dcompiler.h>

#include <cheat-base/events/event.hpp>

namespace backend 
{
	void InitializeDX11Hooks();

	// Thanks to https://github.com/ocornut/imgui/wiki/Image-Loading-and-Displaying-Examples#example-for-directx11-users
	bool LoadTextureFromMemory(LPBYTE image_data, int image_width, int image_height, ID3D11ShaderResourceView** out_srv, int* out_width, int* out_height);

	class DX11Events
	{
	public:
		inline static TEvent<ID3D11DeviceContext*>& RenderEvent = *new TEvent<ID3D11DeviceContext*>();  // Relic: immortal
		inline static TEvent<HWND, ID3D11Device*, ID3D11DeviceContext*, IDXGISwapChain*>& InitializeEvent = *new TEvent<HWND, ID3D11Device*, ID3D11DeviceContext*, IDXGISwapChain*>();  // Relic: immortal
		inline static TEvent<>& FailedEvent = *new TEvent<>();  // Relic: immortal
#if RELIC_DEVTEST
		// DevTest builds only (the dev-only test bed): fired on the render thread right before (afterOverlay false)
		// and right after (true) RenderEvent draws the mod's overlay, with the swap chain being presented.
		inline static TEvent<IDXGISwapChain*, ID3D11DeviceContext*, bool>& DevPresentEvent = *new TEvent<IDXGISwapChain*, ID3D11DeviceContext*, bool>();  // Relic: immortal
#endif
	};
}

#if RELIC_DEVTEST
namespace backend
{
	// The Flags of the Present call the overlay is being drawn for, set by each Present detour on the line of its
	// diag tick (RELIC_DEV_PRESENT_FLAGS). A test present (DXGI_PRESENT_TEST) shows nothing, so the DevTest points
	// are not fired for it: a screenshot is never taken from a frame that is not presented.
	inline thread_local UINT t_DevPresentFlags = 0;

	// The overlay's RenderEvent with the DevTest present points around it.
	inline void DevRenderEvent(IDXGISwapChain* chain, ID3D11DeviceContext* context)
	{
		const bool shown = (t_DevPresentFlags & DXGI_PRESENT_TEST) == 0;
		if (shown)
			DX11Events::DevPresentEvent(chain, context, false);
		DX11Events::RenderEvent(context);
		if (shown)
			DX11Events::DevPresentEvent(chain, context, true);
	}
}
#define RELIC_DEV_PRESENT_FLAGS(flags) (::backend::t_DevPresentFlags = (flags))
#define RELIC_RENDER_EVENT(chain, context) ::backend::DevRenderEvent(chain, context)
#else
#define RELIC_DEV_PRESENT_FLAGS(flags) ((void)0)
#define RELIC_RENDER_EVENT(chain, context) backend::DX11Events::RenderEvent(context)
#endif
