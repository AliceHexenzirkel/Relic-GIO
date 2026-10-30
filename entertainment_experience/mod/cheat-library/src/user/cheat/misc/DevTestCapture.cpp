#include "pch-il2cpp.h"
#include "DevTestCapture.h"

#if RELIC_DEVTEST

#include "DevTestChannel.h"

#include <cheat-base/relic-guard.h>

#include <atomic>
#include <cstring>
#include <vector>

#include <bcrypt.h>
#include <wincodec.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "windowscodecs.lib")

// How a screenshot is taken without ever stalling the game.
//
// The overlay hook calls DX11Events::DevPresentEvent on the render thread twice per presented frame: right before
// the mod draws its overlay (the game's own frame, its UI included unless hidden) and right after - never for a
// test present (DXGI_PRESENT_TEST), which shows nothing. That thread is the Present hook: it must never wait, never
// touch a file and never hold a lock across a D3D call. So a shot is a job handed from thread to thread through one
// slot whose owner is decided by compare-and-swap on its state:
//
//   game thread    Idle -> Armed                         (the request is written first; the slot is the game's)
//   render thread  Armed -> RenderCopy -> Mapping        (after `afterFrames` frames: GetBuffer, an MSAA resolve when
//                                                         needed, CopyResource into a cached STAGING texture)
//                            or -> NeedBuffer            (the CPU copy of this size does not exist yet)
//   worker         NeedBuffer -> WorkerAlloc -> Mapping  (allocates and touches the CPU copy, off the render thread)
//   render thread  Mapping -> RenderMap -> Encode        (Map with DO_NOT_WAIT on a later frame - WAS_STILL_DRAWING
//                            or -> Mapping               means try at the next one - then one row-wise memcpy, Unmap)
//   worker         Encode -> WorkerEncode -> Done        (24-bit BGR through WIC into <stem>.png.tmp, the rename, the
//                                                         sidecar <stem>.json)
//   game thread    Done/Failed -> Idle                   (reads the result and answers the command)
//
// A thread acts only on a state it has claimed, so nobody touches a job another thread holds; the game thread
// cancels a job (its time-out) by claiming it from a waiting state, never from a held one. What a fault leaves
// behind on the render thread (a back buffer reference, a mapped texture) is kept in a variable of its own and put
// right at the start of the next call. Nothing here is allocated per frame: the staging texture is made once per
// size and the CPU copy by the worker (the event that calls OnPresent takes its shared lock and a list node per
// call, as RenderEvent does for the overlay).
namespace cheat::feature::devtest::capture
{
	namespace
	{
		enum State : int
		{
			Idle = 0,
			Armed,          // waiting for its frame (render thread may claim)
			RenderCopy,     // the render thread copies the back buffer
			NeedBuffer,     // the worker may claim: the CPU copy is to be made for this size
			WorkerAlloc,
			Mapping,        // the render thread may claim: the copy is to be read back
			RenderMap,
			Encode,         // the worker may claim: the pixels are in the CPU copy
			WorkerEncode,
			Done,           // the game thread may claim
			Failed,
		};

		struct Job
		{
			Request request;
			int framesSeen = 0;
			D3D11_TEXTURE2D_DESC desc{};
			int64_t copyUs = 0, memcpyUs = 0, encodeMs = 0;
			int mapWaitFrames = 0;
			uint64_t bytes = 0;
			char error[300] = {};   // a fixed-size buffer: the render thread never allocates
		};

		std::atomic<int> g_State{ Idle };
		Job g_Job;

		// render thread only
		ID3D11Texture2D* g_Staging = nullptr;
		ID3D11Texture2D* g_Resolve = nullptr;
		D3D11_TEXTURE2D_DESC g_StagingDesc{};
		ID3D11Texture2D* g_HeldBack = nullptr;   // a back buffer reference a fault left behind
		bool g_Mapped = false;                    // the staging texture a fault left mapped

		// the CPU copy: tightly packed rows of 4 bytes per pixel; made by the worker, filled by the render thread,
		// read by the worker - each only while it holds the job
		std::vector<uint8_t> g_Pixels;
		uint32_t g_PixelsWidth = 0;
		uint32_t g_PixelsHeight = 0;

		// written by the worker before g_HashReady
		std::atomic<bool> g_HashReady{ false };
		char g_Hash[65] = {};
		char g_DllPathUtf8[1024] = {};

		LARGE_INTEGER g_QpcFrequency{};

		void SetError(_Printf_format_string_ const char* format, ...)
		{
			va_list args;
			va_start(args, format);
			_vsnprintf_s(g_Job.error, sizeof(g_Job.error), _TRUNCATE, format, args);
			va_end(args);
		}

		int64_t QpcNow()
		{
			LARGE_INTEGER t{};
			QueryPerformanceCounter(&t);
			return t.QuadPart;
		}

		int64_t UsSince(int64_t start)
		{
			if (g_QpcFrequency.QuadPart <= 0)
				return 0;
			return (QpcNow() - start) * 1000000 / g_QpcFrequency.QuadPart;
		}

		// The 8-bit four-channel formats, typed or typeless: the copy keeps the back buffer's own format (the
		// staging texture is made with it), so the bytes read back are the frame's whatever view the game draws with.
		bool Readable(DXGI_FORMAT f)
		{
			switch (f)
			{
			case DXGI_FORMAT_R8G8B8A8_TYPELESS:
			case DXGI_FORMAT_R8G8B8A8_UNORM:
			case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
			case DXGI_FORMAT_B8G8R8A8_TYPELESS:
			case DXGI_FORMAT_B8G8R8A8_UNORM:
			case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
			case DXGI_FORMAT_B8G8R8X8_TYPELESS:
			case DXGI_FORMAT_B8G8R8X8_UNORM:
			case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
				return true;
			default:
				return false;
			}
		}

		bool BlueFirst(DXGI_FORMAT f)
		{
			return f == DXGI_FORMAT_B8G8R8A8_TYPELESS || f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB
				|| f == DXGI_FORMAT_B8G8R8X8_TYPELESS || f == DXGI_FORMAT_B8G8R8X8_UNORM || f == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
		}

		// The format a multisampled back buffer is resolved with: ResolveSubresource needs a typed one, so a
		// typeless buffer is resolved as the UNORM format of its family (the bytes are copied, not converted).
		DXGI_FORMAT ResolveFormat(DXGI_FORMAT f)
		{
			switch (f)
			{
			case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
			case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
			case DXGI_FORMAT_B8G8R8X8_TYPELESS: return DXGI_FORMAT_B8G8R8X8_UNORM;
			default: return f;
			}
		}

		// ---------------------------------------------------------------------------- render thread

		bool EnsureStaging(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& desc)
		{
			if (g_Staging != nullptr && g_StagingDesc.Width == desc.Width && g_StagingDesc.Height == desc.Height
				&& g_StagingDesc.Format == desc.Format)
				return true;
			if (g_Staging != nullptr)
			{
				g_Staging->Release();
				g_Staging = nullptr;
			}
			if (g_Resolve != nullptr)
			{
				g_Resolve->Release();
				g_Resolve = nullptr;
			}
			D3D11_TEXTURE2D_DESC sd{};
			sd.Width = desc.Width;
			sd.Height = desc.Height;
			sd.MipLevels = 1;
			sd.ArraySize = 1;
			sd.Format = desc.Format;
			sd.SampleDesc.Count = 1;
			sd.Usage = D3D11_USAGE_STAGING;
			sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
			HRESULT hr = device->CreateTexture2D(&sd, nullptr, &g_Staging);
			if (FAILED(hr) || g_Staging == nullptr)
			{
				g_Staging = nullptr;
				SetError("creating the staging texture (%ux%u) failed (0x%08X)", desc.Width, desc.Height, static_cast<unsigned>(hr));
				return false;
			}
			g_StagingDesc = sd;
			return true;
		}

		bool EnsureResolve(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& desc)
		{
			if (g_Resolve != nullptr)
				return true;   // made together with the staging texture of this size
			D3D11_TEXTURE2D_DESC rd{};
			rd.Width = desc.Width;
			rd.Height = desc.Height;
			rd.MipLevels = 1;
			rd.ArraySize = 1;
			rd.Format = desc.Format;
			rd.SampleDesc.Count = 1;
			rd.Usage = D3D11_USAGE_DEFAULT;
			HRESULT hr = device->CreateTexture2D(&rd, nullptr, &g_Resolve);
			if (FAILED(hr) || g_Resolve == nullptr)
			{
				g_Resolve = nullptr;
				SetError("creating the resolve texture failed (0x%08X)", static_cast<unsigned>(hr));
				return false;
			}
			return true;
		}

		int CopyFrame(IDXGISwapChain* chain, ID3D11DeviceContext* context)
		{
			g_Job.framesSeen++;
			if (g_Job.framesSeen < g_Job.request.afterFrames)
				return Armed;

			const int64_t start = QpcNow();
			ID3D11Texture2D* back = nullptr;
			HRESULT hr = chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back));
			if (FAILED(hr) || back == nullptr)
			{
				SetError("IDXGISwapChain::GetBuffer failed (0x%08X)", static_cast<unsigned>(hr));
				return Failed;
			}
			g_HeldBack = back;
			D3D11_TEXTURE2D_DESC desc{};
			back->GetDesc(&desc);
			g_Job.desc = desc;
			if (!Readable(desc.Format))
			{
				back->Release();
				g_HeldBack = nullptr;
				SetError("the back buffer's format (DXGI %u) is not one this capture reads (8-bit RGBA or BGRA)", static_cast<unsigned>(desc.Format));
				return Failed;
			}
			ID3D11Device* device = nullptr;
			back->GetDevice(&device);
			bool ok = device != nullptr && EnsureStaging(device, desc)
				&& (desc.SampleDesc.Count <= 1 || EnsureResolve(device, desc));
			if (ok)
			{
				if (desc.SampleDesc.Count > 1)
				{
					context->ResolveSubresource(g_Resolve, 0, back, 0, ResolveFormat(desc.Format));
					context->CopyResource(g_Staging, g_Resolve);
				}
				else
					context->CopyResource(g_Staging, back);
			}
			else if (device == nullptr)
				SetError("the back buffer has no device");
			if (device != nullptr)
				device->Release();
			back->Release();
			g_HeldBack = nullptr;
			if (!ok)
				return Failed;
			g_Job.copyUs = UsSince(start);
			return g_PixelsWidth == desc.Width && g_PixelsHeight == desc.Height ? Mapping : NeedBuffer;
		}

		int MapFrame(ID3D11DeviceContext* context)
		{
			D3D11_MAPPED_SUBRESOURCE mapped{};
			HRESULT hr = context->Map(g_Staging, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
			if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
			{
				g_Job.mapWaitFrames++;
				return Mapping;
			}
			if (FAILED(hr))
			{
				SetError("ID3D11DeviceContext::Map failed (0x%08X)", static_cast<unsigned>(hr));
				return Failed;
			}
			g_Mapped = true;
			const int64_t start = QpcNow();
			const size_t row = static_cast<size_t>(g_Job.desc.Width) * 4;
			const uint8_t* src = static_cast<const uint8_t*>(mapped.pData);
			uint8_t* dst = g_Pixels.data();
			if (mapped.RowPitch == row)
				memcpy(dst, src, row * g_Job.desc.Height);
			else
				for (UINT y = 0; y < g_Job.desc.Height; y++)
					memcpy(dst + y * row, src + static_cast<size_t>(y) * mapped.RowPitch, row);
			context->Unmap(g_Staging, 0);
			g_Mapped = false;
			g_Job.memcpyUs = UsSince(start);
			return Encode;
		}

		// ---------------------------------------------------------------------------- worker thread

		void HashOwnDll()
		{
			HMODULE self = nullptr;
			wchar_t path[1024] = {};
			if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
					reinterpret_cast<LPCWSTR>(&HashOwnDll), &self)
				|| GetModuleFileNameW(self, path, static_cast<DWORD>(std::size(path))) == 0)
			{
				LogLine("worker", "the DLL's own path is not known (error %lu) - no sha256", GetLastError());
				return;
			}
			WideCharToMultiByte(CP_UTF8, 0, path, -1, g_DllPathUtf8, static_cast<int>(sizeof(g_DllPathUtf8)), nullptr, nullptr);

			BCRYPT_ALG_HANDLE alg = nullptr;
			BCRYPT_HASH_HANDLE hash = nullptr;
			HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
				OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
			bool ok = file != INVALID_HANDLE_VALUE
				&& BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0))
				&& BCRYPT_SUCCESS(BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0));
			std::vector<uint8_t> chunk(1 << 20);
			while (ok)
			{
				DWORD read = 0;
				if (!ReadFile(file, chunk.data(), static_cast<DWORD>(chunk.size()), &read, nullptr))
					ok = false;
				else if (read == 0)
					break;
				else if (!BCRYPT_SUCCESS(BCryptHashData(hash, chunk.data(), read, 0)))
					ok = false;
			}
			uint8_t digest[32] = {};
			if (ok)
				ok = BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof(digest), 0));
			if (hash != nullptr)
				BCryptDestroyHash(hash);
			if (alg != nullptr)
				BCryptCloseAlgorithmProvider(alg, 0);
			if (file != INVALID_HANDLE_VALUE)
				CloseHandle(file);
			if (!ok)
			{
				LogLine("worker", "the DLL could not be hashed (error %lu)", GetLastError());
				return;
			}
			static const char* digits = "0123456789abcdef";
			for (int i = 0; i < 32; i++)
			{
				g_Hash[i * 2] = digits[digest[i] >> 4];
				g_Hash[i * 2 + 1] = digits[digest[i] & 15];
			}
			g_Hash[64] = 0;
			g_HashReady.store(true, std::memory_order_release);
			LogLine("worker", "this DLL: %s sha256 %s", g_DllPathUtf8, g_Hash);
		}

		int Allocate()
		{
			const uint32_t w = g_Job.desc.Width, h = g_Job.desc.Height;
			try
			{
				g_Pixels.assign(static_cast<size_t>(w) * h * 4, 0);   // touches every page here, not on the render thread
			}
			catch (const std::exception&)
			{
				g_PixelsWidth = g_PixelsHeight = 0;
				SetError("no memory for a %ux%u copy", w, h);
				return Failed;
			}
			g_PixelsWidth = w;
			g_PixelsHeight = h;
			return Mapping;
		}

		template <class T>
		void ReleaseCom(T*& p)
		{
			if (p != nullptr)
			{
				p->Release();
				p = nullptr;
			}
		}

		// 24-bit BGR rows through WIC's PNG encoder into `path`. HRESULT of the first step that failed.
		HRESULT EncodePng(IWICImagingFactory* factory, const std::wstring& path, uint32_t w, uint32_t h,
			const std::vector<uint8_t>& bgr, const char*& step)
		{
			IWICStream* stream = nullptr;
			IWICBitmapEncoder* encoder = nullptr;
			IWICBitmapFrameEncode* frame = nullptr;
			IPropertyBag2* props = nullptr;
			HRESULT hr = factory->CreateStream(&stream);
			step = "CreateStream";
			if (SUCCEEDED(hr)) { step = "InitializeFromFilename"; hr = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE); }
			if (SUCCEEDED(hr)) { step = "CreateEncoder"; hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder); }
			if (SUCCEEDED(hr)) { step = "Encoder.Initialize"; hr = encoder->Initialize(stream, WICBitmapEncoderNoCache); }
			if (SUCCEEDED(hr)) { step = "CreateNewFrame"; hr = encoder->CreateNewFrame(&frame, &props); }
			if (SUCCEEDED(hr)) { step = "Frame.Initialize"; hr = frame->Initialize(props); }
			if (SUCCEEDED(hr)) { step = "SetSize"; hr = frame->SetSize(w, h); }
			WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
			if (SUCCEEDED(hr)) { step = "SetPixelFormat"; hr = frame->SetPixelFormat(&format); }
			if (SUCCEEDED(hr) && format != GUID_WICPixelFormat24bppBGR) { step = "SetPixelFormat (24-bit BGR refused)"; hr = E_FAIL; }
			if (SUCCEEDED(hr))
			{
				step = "WritePixels";
				hr = frame->WritePixels(h, w * 3, static_cast<UINT>(bgr.size()), const_cast<BYTE*>(bgr.data()));
			}
			if (SUCCEEDED(hr)) { step = "Frame.Commit"; hr = frame->Commit(); }
			if (SUCCEEDED(hr)) { step = "Encoder.Commit"; hr = encoder->Commit(); }
			ReleaseCom(props);
			ReleaseCom(frame);
			ReleaseCom(encoder);
			ReleaseCom(stream);   // closes the file
			return hr;
		}

		const char* FormatName(uint32_t f)
		{
			switch (f)
			{
			case DXGI_FORMAT_R8G8B8A8_TYPELESS: return "R8G8B8A8_TYPELESS";
			case DXGI_FORMAT_R8G8B8A8_UNORM: return "R8G8B8A8_UNORM";
			case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return "R8G8B8A8_UNORM_SRGB";
			case DXGI_FORMAT_B8G8R8A8_TYPELESS: return "B8G8R8A8_TYPELESS";
			case DXGI_FORMAT_B8G8R8A8_UNORM: return "B8G8R8A8_UNORM";
			case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return "B8G8R8A8_UNORM_SRGB";
			case DXGI_FORMAT_B8G8R8X8_TYPELESS: return "B8G8R8X8_TYPELESS";
			case DXGI_FORMAT_B8G8R8X8_UNORM: return "B8G8R8X8_UNORM";
			case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: return "B8G8R8X8_UNORM_SRGB";
			default: return "other";
			}
		}

		int Write(IWICImagingFactory*& factory)
		{
			const int64_t start = QpcNow();
			const uint32_t w = g_Job.desc.Width, h = g_Job.desc.Height;
			const bool blueFirst = BlueFirst(g_Job.desc.Format);
			std::vector<uint8_t> bgr;
			try
			{
				bgr.resize(static_cast<size_t>(w) * h * 3);
			}
			catch (const std::exception&)
			{
				SetError("no memory for the %ux%u image", w, h);
				return Failed;
			}
			const uint8_t* src = g_Pixels.data();
			uint8_t* dst = bgr.data();
			const size_t pixels = static_cast<size_t>(w) * h;
			for (size_t i = 0; i < pixels; i++, src += 4, dst += 3)
			{
				// the alpha of a back buffer says nothing about the picture: every pixel is written opaque
				dst[0] = blueFirst ? src[0] : src[2];
				dst[1] = src[1];
				dst[2] = blueFirst ? src[2] : src[0];
			}
			if (factory == nullptr)
			{
				HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
				if (FAILED(hr) || factory == nullptr)
				{
					factory = nullptr;
					SetError("the WIC imaging factory is not available (0x%08X)", static_cast<unsigned>(hr));
					return Failed;
				}
			}
			const std::wstring png = g_Job.request.png.wstring();
			const std::wstring tmp = png + L".tmp";
			const char* step = "";
			HRESULT hr = EncodePng(factory, tmp, w, h, bgr, step);
			if (FAILED(hr))
			{
				DeleteFileW(tmp.c_str());
				SetError("PNG encoding failed at %s (0x%08X)", step, static_cast<unsigned>(hr));
				return Failed;
			}
			if (!MoveFileExW(tmp.c_str(), png.c_str(), MOVEFILE_REPLACE_EXISTING))
			{
				DWORD error = GetLastError();
				DeleteFileW(tmp.c_str());
				SetError("the PNG could not take its name (error %lu)", error);
				return Failed;
			}
			WIN32_FILE_ATTRIBUTE_DATA attributes{};
			if (GetFileAttributesExW(png.c_str(), GetFileExInfoStandard, &attributes))
				g_Job.bytes = (static_cast<uint64_t>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow;
			g_Job.encodeMs = UsSince(start) / 1000;

			std::string werr;
			try
			{
				nlohmann::json side = nlohmann::json::parse(g_Job.request.sidecarBase, nullptr, false);
				if (!side.is_object())
					side = nlohmann::json::object();
				side["file"] = Utf8(g_Job.request.png.filename());
				side["width"] = w;
				side["height"] = h;
				side["format"] = static_cast<uint32_t>(g_Job.desc.Format);
				side["formatName"] = FormatName(g_Job.desc.Format);
				side["samples"] = g_Job.desc.SampleDesc.Count;
				side["copyUs"] = g_Job.copyUs;
				side["mapWaitFrames"] = g_Job.mapWaitFrames;
				side["memcpyUs"] = g_Job.memcpyUs;
				side["encodeMs"] = g_Job.encodeMs;
				side["bytes"] = g_Job.bytes;
				if (!WriteFileAtomic(g_Job.request.sidecar, Dump(side, 2), werr))
				{
					SetError("the PNG is written but its sidecar is not: %.200s", werr.c_str());
					return Failed;
				}
			}
			catch (const std::exception& e)
			{
				SetError("the sidecar could not be put together: %.200s", e.what());
				return Failed;
			}
			LogLine("worker", "shot %lld: %ux%u %s, %llu bytes (copy %lld us, map wait %d frames, memcpy %lld us, encode %lld ms)",
				static_cast<long long>(g_Job.request.id), w, h, FormatName(g_Job.desc.Format),
				static_cast<unsigned long long>(g_Job.bytes), static_cast<long long>(g_Job.copyUs), g_Job.mapWaitFrames,
				static_cast<long long>(g_Job.memcpyUs), static_cast<long long>(g_Job.encodeMs));
			return Done;
		}

		DWORD WINAPI Worker(LPVOID)
		{
			HashOwnDll();
			HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
			if (FAILED(co) && co != RPC_E_CHANGED_MODE)
				LogLine("worker", "COM did not start on the worker (0x%08X) - shots will fail", static_cast<unsigned>(co));
			IWICImagingFactory* factory = nullptr;
			for (;;)
			{
				Sleep(10);
				int s = g_State.load(std::memory_order_acquire);
				if (s == NeedBuffer)
				{
					if (g_State.compare_exchange_strong(s, WorkerAlloc, std::memory_order_acq_rel))
						g_State.store(Allocate(), std::memory_order_release);
				}
				else if (s == Encode)
				{
					if (g_State.compare_exchange_strong(s, WorkerEncode, std::memory_order_acq_rel))
					{
						int next = Failed;
						try
						{
							next = Write(factory);
						}
						catch (const std::exception& e)
						{
							SetError("the image could not be written: %.200s", e.what());
						}
						if (next == Failed)
							LogLine("worker", "shot %lld failed: %s", static_cast<long long>(g_Job.request.id), g_Job.error);
						g_State.store(next, std::memory_order_release);
					}
				}
			}
			return 0;
		}
	}

	void Start()
	{
		static bool started = false;
		if (started)
			return;
		started = true;
		QueryPerformanceFrequency(&g_QpcFrequency);
		HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
		if (thread != nullptr)
			CloseHandle(thread);
		else
			LogLine("game", "the worker thread did not start (error %lu) - no shots and no sha256", GetLastError());
	}

	void OnPresent(IDXGISwapChain* chain, ID3D11DeviceContext* context, bool afterOverlay)
	{
		if (context == nullptr || chain == nullptr)
			return;
		if (g_Mapped || g_HeldBack != nullptr)
		{
			relic::Try([&]()
			{
				if (g_Mapped && g_Staging != nullptr)
					context->Unmap(g_Staging, 0);
				if (g_HeldBack != nullptr)
					g_HeldBack->Release();
			});
			g_Mapped = false;
			g_HeldBack = nullptr;
		}
		int s = g_State.load(std::memory_order_acquire);
		if (s != Armed && s != Mapping)
			return;
		const int held = s == Armed ? RenderCopy : RenderMap;
		if (!g_State.compare_exchange_strong(s, held, std::memory_order_acq_rel))
			return;
		if (g_Job.request.afterOverlay != afterOverlay)
		{
			g_State.store(s, std::memory_order_release);   // the other capture point of this frame is the job's
			return;
		}
		int next = Failed;
		if (!relic::Try([&]() { next = held == RenderCopy ? CopyFrame(chain, context) : MapFrame(context); }))
		{
			SetError("the copy faulted on the render thread (0x%08X)", relic::g_lastGuardCode);
			next = Failed;
		}
		g_State.store(next, std::memory_order_release);
	}

	bool Arm(const Request& request, std::string& error)
	{
		if (g_State.load(std::memory_order_acquire) != Idle)
		{
			error = "a shot is already in flight";
			return false;
		}
		g_Job.request = request;
		g_Job.framesSeen = 0;
		g_Job.desc = {};
		g_Job.copyUs = g_Job.memcpyUs = g_Job.encodeMs = 0;
		g_Job.mapWaitFrames = 0;
		g_Job.bytes = 0;
		g_Job.error[0] = 0;
		g_State.store(Armed, std::memory_order_release);
		return true;
	}

	Phase Poll()
	{
		switch (g_State.load(std::memory_order_acquire))
		{
		case Idle: return Phase::Idle;
		case Armed:
		case RenderCopy: return Phase::Waiting;
		case Done: return Phase::Done;
		case Failed: return Phase::Failed;
		default: return Phase::Copied;
		}
	}

	bool Take(Result& out)
	{
		int s = g_State.load(std::memory_order_acquire);
		if (s != Done && s != Failed)
			return false;
		out = Result{};
		out.error = s == Failed ? (g_Job.error[0] != 0 ? std::string(g_Job.error) : std::string("the shot failed")) : std::string();
		out.width = g_Job.desc.Width;
		out.height = g_Job.desc.Height;
		out.format = static_cast<uint32_t>(g_Job.desc.Format);
		out.samples = g_Job.desc.SampleDesc.Count;
		out.copyUs = g_Job.copyUs;
		out.memcpyUs = g_Job.memcpyUs;
		out.encodeMs = g_Job.encodeMs;
		out.mapWaitFrames = g_Job.mapWaitFrames;
		out.bytes = g_Job.bytes;
		g_State.store(Idle, std::memory_order_release);
		return true;
	}

	bool Cancel()
	{
		int s = g_State.load(std::memory_order_acquire);
		if (s == Idle)
			return true;
		if (s == Done || s == Failed)
		{
			g_State.store(Idle, std::memory_order_release);
			return true;
		}
		if (s == Armed || s == NeedBuffer || s == Mapping || s == Encode)
			return g_State.compare_exchange_strong(s, Idle, std::memory_order_acq_rel);
		return false;   // a thread holds it for the moment
	}

	std::string DllSha256()
	{
		return g_HashReady.load(std::memory_order_acquire) ? std::string(g_Hash) : std::string();
	}

	std::string DllPath()
	{
		return g_HashReady.load(std::memory_order_acquire) ? std::string(g_DllPathUtf8) : std::string();
	}
}
#endif
