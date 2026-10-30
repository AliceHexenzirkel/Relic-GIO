#pragma once

#if RELIC_DEVTEST
#include <cstdint>
#include <filesystem>
#include <string>

#include <d3d11.h>

// The screenshots of the dev-only test bed (DevTest.cpp): the back buffer as the game presents it, copied on the
// render thread and written as a PNG by a worker thread of its own. See the .cpp.
namespace cheat::feature::devtest::capture
{
	// One screenshot, as the game thread asks for it.
	struct Request
	{
		int64_t id = 0;
		bool afterOverlay = false;        // false: the game's frame before the mod draws; true: with the mod's overlay
		int afterFrames = 2;              // presented frames to let pass first (the frame that shows a change made now)
		std::filesystem::path png;        // the file to write
		std::filesystem::path sidecar;    // <stem>.json beside it
		std::string sidecarBase;          // JSON object text: what the game thread knew when it asked
	};

	// Where the one job is, as the game thread sees it.
	enum class Phase { Idle, Waiting, Copied, Done, Failed };

	struct Result
	{
		std::string error;                // empty: the PNG and its sidecar are written
		uint32_t width = 0, height = 0, format = 0, samples = 0;
		int64_t copyUs = 0, memcpyUs = 0, encodeMs = 0;
		int mapWaitFrames = 0;
		uint64_t bytes = 0;
	};

	// The worker thread: the DLL's own sha256 first, then every encode. Called once, from the feature's constructor.
	void Start();

	// The render thread: DX11Events::DevPresentEvent, right before and right after the overlay is drawn.
	void OnPresent(IDXGISwapChain* chain, ID3D11DeviceContext* context, bool afterOverlay);

	// The game thread.
	bool Arm(const Request& request, std::string& error);   // false while a job is in flight
	Phase Poll();
	bool Take(Result& out);    // a job that is done or failed: its result, and the slot is free again
	bool Cancel();             // true once the slot is free; false while another thread holds the job (try again)

	// Any thread: the sha256 of the DLL file this code runs from ("" until the worker has read it) and its path.
	std::string DllSha256();
	std::string DllPath();
}
#endif
