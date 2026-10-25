// PSDTool Viewer のエクスプローラー用サムネイル拡張。
// .ptv は先頭に格納した thumbnail.png（保存時の見た目）を、.psd は統合画像（保存時の見た目）を縮小して返す。
// エクスプローラー（サムネイル用の別プロセス）に読み込まれるため、.NET を使わず単体で動くようにしている。

#include <windows.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wincodec.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <new>
#include <vector>

#include <shlobj.h>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "windowscodecs.lib")

namespace
{
    // {7CF7A05F-EE5E-4CAA-B07C-79F8C1795C96}（インストーラーの登録と一致させる）
    const CLSID CLSID_ThumbnailProvider = { 0x7CF7A05F, 0xEE5E, 0x4CAA, { 0xB0, 0x7C, 0x79, 0xF8, 0xC1, 0x79, 0x5C, 0x96 } };

    long g_objects = 0;
    long g_locks = 0;

    // エクスプローラーを重くしないよう、極端に大きい画像は扱わない
    constexpr uint64_t MaxPixels = 200'000'000;
    constexpr uint32_t MaxThumbnailBytes = 64 * 1024 * 1024;

    template <class T>
    void SafeRelease(T*& p)
    {
        if (p)
        {
            p->Release();
            p = nullptr;
        }
    }

    uint16_t ReadU16BE(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
    uint32_t ReadU32BE(const uint8_t* p) { return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) | (static_cast<uint32_t>(p[2]) << 8) | p[3]; }
    uint16_t ReadU16LE(const uint8_t* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }
    uint32_t ReadU32LE(const uint8_t* p) { return p[0] | (static_cast<uint32_t>(p[1]) << 8) | (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24); }

    HRESULT ReadAt(IStream* stream, uint64_t offset, void* buffer, ULONG size)
    {
        LARGE_INTEGER position;
        position.QuadPart = static_cast<LONGLONG>(offset);
        HRESULT hr = stream->Seek(position, STREAM_SEEK_SET, nullptr);
        if (FAILED(hr)) return hr;
        ULONG total = 0;
        while (total < size)
        {
            ULONG read = 0;
            hr = stream->Read(static_cast<uint8_t*>(buffer) + total, size - total, &read);
            if (FAILED(hr)) return hr;
            if (read == 0) return E_FAIL;
            total += read;
        }
        return S_OK;
    }

    HRESULT GetStreamSize(IStream* stream, uint64_t* size)
    {
        STATSTG stat = {};
        HRESULT hr = stream->Stat(&stat, STATFLAG_NONAME);
        if (FAILED(hr)) return hr;
        *size = stat.cbSize.QuadPart;
        return S_OK;
    }

    // 縦横の長い方が cx に収まる大きさ（拡大はしない）
    void FitSize(uint32_t width, uint32_t height, UINT cx, uint32_t* tw, uint32_t* th)
    {
        if (width <= cx && height <= cx)
        {
            *tw = width;
            *th = height;
        }
        else if (width >= height)
        {
            *tw = cx;
            *th = std::max(1u, static_cast<uint32_t>(static_cast<uint64_t>(height) * cx / width));
        }
        else
        {
            *th = cx;
            *tw = std::max(1u, static_cast<uint32_t>(static_cast<uint64_t>(width) * cx / height));
        }
    }

    HRESULT CreateDib(uint32_t width, uint32_t height, HBITMAP* bitmap, void** bits)
    {
        BITMAPINFO info = {};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = static_cast<LONG>(width);
        info.bmiHeader.biHeight = -static_cast<LONG>(height); // 上の行から順に並べる
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        *bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, bits, nullptr, 0);
        return *bitmap ? S_OK : E_OUTOFMEMORY;
    }

    // ===== .ptv（ZIP） =====

    // 中央ディレクトリから thumbnail.png（無圧縮で格納）を探して読み出す
    HRESULT ReadPtvThumbnail(IStream* stream, std::vector<uint8_t>& png)
    {
        uint64_t size = 0;
        HRESULT hr = GetStreamSize(stream, &size);
        if (FAILED(hr)) return hr;
        if (size < 22) return E_FAIL;

        const uint32_t tailSize = static_cast<uint32_t>(std::min<uint64_t>(size, 65557));
        std::vector<uint8_t> tail(tailSize);
        hr = ReadAt(stream, size - tailSize, tail.data(), tailSize);
        if (FAILED(hr)) return hr;
        int64_t eocd = -1;
        for (int64_t i = static_cast<int64_t>(tailSize) - 22; i >= 0; i--)
        {
            if (ReadU32LE(&tail[static_cast<size_t>(i)]) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0) return E_FAIL;
        const uint32_t cdSize = ReadU32LE(&tail[static_cast<size_t>(eocd) + 12]);
        const uint32_t cdOffset = ReadU32LE(&tail[static_cast<size_t>(eocd) + 16]);
        if (cdSize == 0 || cdSize > MaxThumbnailBytes || static_cast<uint64_t>(cdOffset) + cdSize > size) return E_FAIL;

        std::vector<uint8_t> cd(cdSize);
        hr = ReadAt(stream, cdOffset, cd.data(), cdSize);
        if (FAILED(hr)) return hr;

        static const char Name[] = "thumbnail.png";
        constexpr uint32_t NameLength = sizeof(Name) - 1;
        for (uint32_t p = 0; p + 46 <= cdSize;)
        {
            if (ReadU32LE(&cd[p]) != 0x02014b50) return E_FAIL;
            const uint16_t method = ReadU16LE(&cd[p + 10]);
            const uint32_t compressed = ReadU32LE(&cd[p + 20]);
            const uint16_t nameLength = ReadU16LE(&cd[p + 28]);
            const uint16_t extraLength = ReadU16LE(&cd[p + 30]);
            const uint16_t commentLength = ReadU16LE(&cd[p + 32]);
            const uint32_t localOffset = ReadU32LE(&cd[p + 42]);
            if (p + 46 + nameLength > cdSize) return E_FAIL;
            if (nameLength == NameLength && memcmp(&cd[p + 46], Name, NameLength) == 0)
            {
                if (method != 0 || compressed == 0 || compressed > MaxThumbnailBytes) return E_FAIL;
                uint8_t local[30];
                hr = ReadAt(stream, localOffset, local, sizeof(local));
                if (FAILED(hr)) return hr;
                if (ReadU32LE(local) != 0x04034b50) return E_FAIL;
                const uint64_t dataOffset = static_cast<uint64_t>(localOffset) + 30 + ReadU16LE(&local[26]) + ReadU16LE(&local[28]);
                png.resize(compressed);
                return ReadAt(stream, dataOffset, png.data(), compressed);
            }
            p += 46u + nameLength + extraLength + commentLength;
        }
        return E_FAIL;
    }

    HRESULT DecodePng(const std::vector<uint8_t>& png, UINT cx, HBITMAP* bitmap)
    {
        IWICImagingFactory* factory = nullptr;
        IStream* memory = nullptr;
        IWICBitmapDecoder* decoder = nullptr;
        IWICBitmapFrameDecode* frame = nullptr;
        IWICFormatConverter* premultiplied = nullptr;
        IWICBitmapScaler* scaler = nullptr;
        IWICFormatConverter* straight = nullptr;
        UINT width = 0, height = 0;
        uint32_t tw = 0, th = 0;
        void* bits = nullptr;

        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr))
        {
            memory = SHCreateMemStream(png.data(), static_cast<UINT>(png.size()));
            hr = memory ? S_OK : E_OUTOFMEMORY;
        }
        if (SUCCEEDED(hr)) hr = factory->CreateDecoderFromStream(memory, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
        if (SUCCEEDED(hr)) hr = decoder->GetFrame(0, &frame);
        if (SUCCEEDED(hr)) hr = frame->GetSize(&width, &height);
        // 透明部分の色がにじまないよう、乗算済みにしてから縮小する
        if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&premultiplied);
        if (SUCCEEDED(hr)) hr = premultiplied->Initialize(frame, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr))
        {
            FitSize(width, height, cx, &tw, &th);
            hr = factory->CreateBitmapScaler(&scaler);
        }
        if (SUCCEEDED(hr)) hr = scaler->Initialize(premultiplied, tw, th, WICBitmapInterpolationModeFant);
        if (SUCCEEDED(hr)) hr = factory->CreateFormatConverter(&straight);
        if (SUCCEEDED(hr)) hr = straight->Initialize(scaler, GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr)) hr = CreateDib(tw, th, bitmap, &bits);
        if (SUCCEEDED(hr))
        {
            hr = straight->CopyPixels(nullptr, tw * 4, tw * th * 4, static_cast<BYTE*>(bits));
            if (FAILED(hr))
            {
                DeleteObject(*bitmap);
                *bitmap = nullptr;
            }
        }

        SafeRelease(straight);
        SafeRelease(scaler);
        SafeRelease(premultiplied);
        SafeRelease(frame);
        SafeRelease(decoder);
        SafeRelease(memory);
        SafeRelease(factory);
        return hr;
    }

    // ===== .psd（統合画像） =====

    bool UnpackBits(const uint8_t* src, size_t srcLength, uint8_t* dst, size_t dstLength)
    {
        size_t s = 0, d = 0;
        while (d < dstLength && s < srcLength)
        {
            const int n = static_cast<int8_t>(src[s++]);
            if (n >= 0)
            {
                const size_t count = static_cast<size_t>(n) + 1;
                if (s + count > srcLength || d + count > dstLength) return false;
                memcpy(dst + d, src + s, count);
                s += count;
                d += count;
            }
            else if (n != -128)
            {
                const size_t count = static_cast<size_t>(1 - n);
                if (s >= srcLength || d + count > dstLength) return false;
                memset(dst + d, src[s++], count);
                d += count;
            }
        }
        return d == dstLength;
    }

    // 統合画像を読みながら縮小先の画素ごとに合計する（全体を展開しないので大きな PSD でもメモリを使わない）
    class Accumulator
    {
    public:
        Accumulator(uint32_t width, uint32_t height, uint32_t tw, uint32_t th)
            : m_tw(tw), m_th(th), m_colMap(width), m_rowMap(height), m_colCount(tw), m_rowCount(th), m_sums(static_cast<size_t>(tw) * th * 4)
        {
            for (uint32_t x = 0; x < width; x++)
            {
                m_colMap[x] = static_cast<uint32_t>(static_cast<uint64_t>(x) * tw / width);
                m_colCount[m_colMap[x]]++;
            }
            for (uint32_t y = 0; y < height; y++)
            {
                m_rowMap[y] = static_cast<uint32_t>(static_cast<uint64_t>(y) * th / height);
                m_rowCount[m_rowMap[y]]++;
            }
        }

        void AddRow(uint32_t channel, uint32_t y, const uint8_t* row)
        {
            uint32_t* target = &m_sums[static_cast<size_t>(m_rowMap[y]) * m_tw * 4];
            for (size_t x = 0; x < m_colMap.size(); x++)
                target[m_colMap[x] * 4 + channel] += row[x];
        }

        // Photoshop と同じく統合画像は白と合成した色で保存されているので、
        // 平均した色と透明度から乗算済みの平均を求め、通常の色に戻す
        void Write(uint8_t* bgra, bool hasAlpha) const
        {
            for (uint32_t ty = 0; ty < m_th; ty++)
            {
                for (uint32_t tx = 0; tx < m_tw; tx++)
                {
                    const uint32_t* s = &m_sums[(static_cast<size_t>(ty) * m_tw + tx) * 4];
                    const uint64_t n = static_cast<uint64_t>(m_colCount[tx]) * m_rowCount[ty];
                    int r = static_cast<int>(s[0] / n), g = static_cast<int>(s[1] / n), b = static_cast<int>(s[2] / n);
                    const int a = hasAlpha ? static_cast<int>(s[3] / n) : 255;
                    if (hasAlpha && a < 255)
                    {
                        auto unmatte = [a](int matted) { const int premultiplied = std::clamp(matted - (255 - a), 0, a); return a > 0 ? premultiplied * 255 / a : 0; };
                        r = unmatte(r);
                        g = unmatte(g);
                        b = unmatte(b);
                    }
                    uint8_t* o = &bgra[(static_cast<size_t>(ty) * m_tw + tx) * 4];
                    o[0] = static_cast<uint8_t>(b);
                    o[1] = static_cast<uint8_t>(g);
                    o[2] = static_cast<uint8_t>(r);
                    o[3] = static_cast<uint8_t>(a);
                }
            }
        }

    private:
        uint32_t m_tw, m_th;
        std::vector<uint32_t> m_colMap, m_rowMap, m_colCount, m_rowCount, m_sums;
    };

    // 画像リソースから ICC プロファイル（ID 0x040F）を取り出す
    void FindIccProfile(const std::vector<uint8_t>& resources, std::vector<uint8_t>& icc)
    {
        size_t p = 0;
        while (p + 12 <= resources.size())
        {
            if (memcmp(&resources[p], "8BIM", 4) != 0) return;
            const uint16_t id = ReadU16BE(&resources[p + 4]);
            size_t q = p + 6;
            const size_t nameLength = resources[q];
            q += (nameLength + 2) & ~static_cast<size_t>(1); // 長さの 1 バイトを含めて偶数に揃える
            if (q + 4 > resources.size()) return;
            const uint32_t size = ReadU32BE(&resources[q]);
            q += 4;
            if (q + size > resources.size()) return;
            if (id == 0x040F)
            {
                icc.assign(resources.begin() + q, resources.begin() + q + size);
                return;
            }
            p = q + ((size + 1) & ~1u);
        }
    }

    // 埋め込みプロファイルの色を sRGB に変換する（失敗したらそのまま）
    void ConvertToSrgb(uint8_t* bgra, uint32_t width, uint32_t height, const std::vector<uint8_t>& icc)
    {
        IWICImagingFactory* factory = nullptr;
        IWICBitmap* source = nullptr;
        IWICColorContext* from = nullptr;
        IWICColorContext* to = nullptr;
        IWICColorTransform* transform = nullptr;
        const UINT stride = width * 4, size = stride * height;
        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->CreateBitmapFromMemory(width, height, GUID_WICPixelFormat32bppBGRA, stride, size, bgra, &source);
        if (SUCCEEDED(hr)) hr = factory->CreateColorContext(&from);
        if (SUCCEEDED(hr)) hr = from->InitializeFromMemory(icc.data(), static_cast<UINT>(icc.size()));
        if (SUCCEEDED(hr)) hr = factory->CreateColorContext(&to);
        if (SUCCEEDED(hr)) hr = to->InitializeFromExifColorSpace(1); // sRGB
        if (SUCCEEDED(hr)) hr = factory->CreateColorTransformer(&transform);
        if (SUCCEEDED(hr)) hr = transform->Initialize(source, from, to, GUID_WICPixelFormat32bppBGRA);
        if (SUCCEEDED(hr))
        {
            std::vector<uint8_t> converted(size);
            if (SUCCEEDED(transform->CopyPixels(nullptr, stride, size, converted.data())))
            {
                // 透明度は元のまま使う
                for (size_t i = 0; i < converted.size(); i += 4)
                {
                    bgra[i] = converted[i];
                    bgra[i + 1] = converted[i + 1];
                    bgra[i + 2] = converted[i + 2];
                }
            }
        }
        SafeRelease(transform);
        SafeRelease(to);
        SafeRelease(from);
        SafeRelease(source);
        SafeRelease(factory);
    }

    HRESULT RenderPsd(IStream* stream, UINT cx, HBITMAP* bitmap)
    {
        uint8_t header[26];
        HRESULT hr = ReadAt(stream, 0, header, sizeof(header));
        if (FAILED(hr)) return hr;
        if (memcmp(header, "8BPS", 4) != 0 || ReadU16BE(&header[4]) != 1) return E_FAIL; // PSB は対象外
        const uint32_t channels = ReadU16BE(&header[12]);
        const uint32_t height = ReadU32BE(&header[14]);
        const uint32_t width = ReadU32BE(&header[18]);
        if (ReadU16BE(&header[22]) != 8 || ReadU16BE(&header[24]) != 3 || channels < 3) return E_FAIL; // 8bit RGB のみ
        if (width == 0 || height == 0 || static_cast<uint64_t>(width) * height > MaxPixels) return E_FAIL;

        // カラーモード・画像リソース・レイヤーとマスク情報を読み飛ばす（画像リソースからは ICC プロファイルを取り出す）
        uint64_t position = 26;
        int layerCount = 0;
        uint8_t buffer[6];
        std::vector<uint8_t> icc;
        for (int section = 0; section < 3; section++)
        {
            hr = ReadAt(stream, position, buffer, 4);
            if (FAILED(hr)) return hr;
            const uint32_t length = ReadU32BE(buffer);
            if (section == 1 && length > 0 && length <= 16 * 1024 * 1024)
            {
                std::vector<uint8_t> resources(length);
                if (SUCCEEDED(ReadAt(stream, position + 4, resources.data(), length))) FindIccProfile(resources, icc);
            }
            if (section == 2 && length >= 6)
            {
                hr = ReadAt(stream, position + 4, buffer, 6);
                if (FAILED(hr)) return hr;
                if (ReadU32BE(buffer) >= 2) layerCount = static_cast<int16_t>(ReadU16BE(&buffer[4]));
            }
            position += 4 + static_cast<uint64_t>(length);
        }
        // レイヤー数が負のとき、統合画像の 4 番目のチャンネルが透明度
        const bool hasAlpha = layerCount < 0 && channels >= 4;
        const uint32_t used = hasAlpha ? 4 : 3;

        hr = ReadAt(stream, position, buffer, 2);
        if (FAILED(hr)) return hr;
        const uint16_t compression = ReadU16BE(buffer);
        position += 2;
        if (compression > 1) return E_FAIL;

        uint32_t tw = 0, th = 0;
        FitSize(width, height, cx, &tw, &th);
        Accumulator accumulator(width, height, tw, th);
        std::vector<uint8_t> row(width);

        if (compression == 1)
        {
            // 全チャンネル分の行ごとの長さ → チャンネルごとの圧縮データ
            std::vector<uint8_t> counts(static_cast<size_t>(channels) * height * 2);
            hr = ReadAt(stream, position, counts.data(), static_cast<ULONG>(counts.size()));
            if (FAILED(hr)) return hr;
            position += counts.size();
            std::vector<uint8_t> packed;
            for (uint32_t c = 0; c < used; c++)
            {
                uint64_t channelBytes = 0;
                for (uint32_t y = 0; y < height; y++) channelBytes += ReadU16BE(&counts[(static_cast<size_t>(c) * height + y) * 2]);
                if (channelBytes > 0x7fffffff) return E_FAIL;
                packed.resize(static_cast<size_t>(channelBytes));
                hr = ReadAt(stream, position, packed.data(), static_cast<ULONG>(channelBytes));
                if (FAILED(hr)) return hr;
                size_t offset = 0;
                for (uint32_t y = 0; y < height; y++)
                {
                    const uint32_t length = ReadU16BE(&counts[(static_cast<size_t>(c) * height + y) * 2]);
                    if (!UnpackBits(packed.data() + offset, length, row.data(), width)) return E_FAIL;
                    offset += length;
                    accumulator.AddRow(c, y, row.data());
                }
                position += channelBytes;
            }
        }
        else
        {
            // 無圧縮：チャンネルごとに width × height。まとめて読みすぎないよう行の束ごとに読む
            const uint32_t rowsPerChunk = std::max(1u, (4u * 1024 * 1024) / width);
            std::vector<uint8_t> chunk;
            for (uint32_t c = 0; c < used; c++)
            {
                for (uint32_t y = 0; y < height; y += rowsPerChunk)
                {
                    const uint32_t rows = std::min(rowsPerChunk, height - y);
                    chunk.resize(static_cast<size_t>(rows) * width);
                    hr = ReadAt(stream, position + (static_cast<uint64_t>(c) * height + y) * width, chunk.data(), static_cast<ULONG>(chunk.size()));
                    if (FAILED(hr)) return hr;
                    for (uint32_t i = 0; i < rows; i++) accumulator.AddRow(c, y + i, chunk.data() + static_cast<size_t>(i) * width);
                }
            }
        }

        void* bits = nullptr;
        hr = CreateDib(tw, th, bitmap, &bits);
        if (FAILED(hr)) return hr;
        accumulator.Write(static_cast<uint8_t*>(bits), hasAlpha);
        if (!icc.empty()) ConvertToSrgb(static_cast<uint8_t*>(bits), tw, th, icc);
        return S_OK;
    }

    // ===== COM オブジェクト =====

    class ThumbnailProvider final : public IInitializeWithStream, public IThumbnailProvider
    {
    public:
        ThumbnailProvider() { InterlockedIncrement(&g_objects); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            static const QITAB table[] = {
                QITABENT(ThumbnailProvider, IInitializeWithStream),
                QITABENT(ThumbnailProvider, IThumbnailProvider),
                { nullptr, 0 },
            };
            return QISearch(this, table, riid, ppv);
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long ref = InterlockedDecrement(&m_ref);
            if (ref == 0) delete this;
            return ref;
        }

        IFACEMETHODIMP Initialize(IStream* stream, DWORD) override
        {
            if (m_stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            return stream->QueryInterface(IID_PPV_ARGS(&m_stream));
        }

        IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alphaType) override
        {
            if (!bitmap || !alphaType) return E_POINTER;
            *bitmap = nullptr;
            *alphaType = WTSAT_ARGB;
            if (!m_stream || cx == 0) return E_UNEXPECTED;
            try
            {
                uint8_t signature[4];
                HRESULT hr = ReadAt(m_stream, 0, signature, sizeof(signature));
                if (FAILED(hr)) return hr;
                if (memcmp(signature, "8BPS", 4) == 0) return RenderPsd(m_stream, cx, bitmap);
                if (ReadU32LE(signature) == 0x04034b50)
                {
                    std::vector<uint8_t> png;
                    hr = ReadPtvThumbnail(m_stream, png);
                    if (FAILED(hr)) return hr;
                    return DecodePng(png, cx, bitmap);
                }
                return E_FAIL;
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
        }

    private:
        ~ThumbnailProvider()
        {
            SafeRelease(m_stream);
            InterlockedDecrement(&g_objects);
        }

        long m_ref = 1;
        IStream* m_stream = nullptr;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() { InterlockedIncrement(&g_objects); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            static const QITAB table[] = {
                QITABENT(ClassFactory, IClassFactory),
                { nullptr, 0 },
            };
            return QISearch(this, table, riid, ppv);
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long ref = InterlockedDecrement(&m_ref);
            if (ref == 0) delete this;
            return ref;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            *ppv = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            auto* provider = new (std::nothrow) ThumbnailProvider();
            if (!provider) return E_OUTOFMEMORY;
            const HRESULT hr = provider->QueryInterface(riid, ppv);
            provider->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock) InterlockedIncrement(&g_locks);
            else InterlockedDecrement(&g_locks);
            return S_OK;
        }

    private:
        ~ClassFactory() { InterlockedDecrement(&g_objects); }

        long m_ref = 1;
    };
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_ThumbnailProvider)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}

// インストーラーの終わりに呼ばれ、関連付け・サムネイル拡張の変更をエクスプローラーに知らせる
// （Windows インストーラーのカスタムアクションの形：UINT __stdcall (MSIHANDLE)）
extern "C" UINT __stdcall RefreshShell(unsigned long)
{
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return ERROR_SUCCESS;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
