namespace Kiriha.Services;

/// <summary>
/// Exif の Orientation（向き）タグを読み書きする。
///
/// ギャラリーの回転は「無劣化で即時保存」が要件なので、画素には一切触れない。
/// JPEG を回転させる一般的な方法は「デコード → 回転 → 再エンコード」だが、それでは
/// 非可逆圧縮をやり直すことになり、回転のたびに画質が落ちる。ここでは代わりに
/// Exif の Orientation（IFD0 タグ 0x0112、SHORT 1 個）だけを書き換える。実体は
/// ファイル内の 2 バイトの上書きなので、圧縮データは 1 バイトも変わらず、書き込みも一瞬で終わる。
/// エクスプローラー・フォト・各種ビューアーはこのタグを尊重するため、見た目は即座に回る。
///
/// Orientation を持たない JPEG（スクリーンショット等）には、Orientation だけを含む
/// 最小限の Exif APP1 セグメントを SOI 直後へ挿入する。圧縮データ本体は移動するだけで
/// 書き換えないので、こちらも無劣化。
///
/// Exif APP1 はあるのに Orientation タグが無い場合は、IFD へエントリーを挿入すると
/// TIFF 内のオフセットを全て貼り直す必要があり（サブ IFD も含む）、失敗時の破損リスクが
/// 割に合わないため対象外にする。呼び出し側は false を受けてエラー表示へ回す。
/// </summary>
internal static class ExifOrientationService
{
    /// <summary>Exif Orientation を持てる（＝無劣化回転できる）拡張子。</summary>
    private static readonly string[] RotatableExtensions =
        [".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff"];

    /// <summary>Exif の Orientation タグ（IFD0）。</summary>
    private const ushort OrientationTag = 0x0112;

    /// <summary>SHORT 型（2 バイト）。Orientation は必ずこの型。</summary>
    private const ushort TypeShort = 3;

    /// <summary>右 90 度回転で Orientation がどう遷移するか（添字 1..8）。</summary>
    private static readonly int[] ClockwiseMap = [0, 6, 7, 8, 5, 2, 3, 4, 1];

    /// <summary>左 90 度回転で Orientation がどう遷移するか（添字 1..8）。</summary>
    private static readonly int[] CounterClockwiseMap = [0, 8, 5, 6, 7, 4, 1, 2, 3];

    /// <summary>この拡張子を無劣化回転の対象にできるか（実際に回せるかはファイル内容次第）。</summary>
    public static bool CanRotate(string extension)
        => Array.IndexOf(RotatableExtensions, extension) >= 0;

    /// <summary>Exif の Orientation を読む。無い / 未対応形式なら 1（正立）を返す。</summary>
    public static int ReadOrientation(byte[] bytes)
        => TryLocate(bytes, out var location) ? location.Value : 1;

    /// <summary>Orientation を右 / 左 90 度ぶん進めた値へ書き換える。成功したら true。</summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="clockwise">true で右回り、false で左回り。</param>
    public static bool TryRotate(string path, bool clockwise)
    {
        try
        {
            if (ImageDecodeService.TryReadAllBytes(path) is not { } bytes)
            {
                return false;
            }

            if (TryLocate(bytes, out var location))
            {
                var next = Advance(location.Value, clockwise);
                WriteOrientationInPlace(path, location.ValueOffset, location.BigEndian, next);
                return true;
            }

            // Exif が無い JPEG には Orientation だけの APP1 を差し込む（圧縮データは書き換えない）。
            if (!HasExifSegment(bytes) && IsJpeg(bytes))
            {
                var next = Advance(1, clockwise);
                InsertOrientationApp1(path, bytes, next);
                return true;
            }

            Logger.Log($"Orientation タグが無いため無劣化回転できません: {path}", LogLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogException($"画像の回転に失敗しました: {path}", ex);
            return false;
        }
    }

    /// <summary>Orientation 値を 90 度ぶん進める。</summary>
    private static int Advance(int current, bool clockwise)
    {
        var value = current is >= 1 and <= 8 ? current : 1;
        return clockwise ? ClockwiseMap[value] : CounterClockwiseMap[value];
    }

    /// <summary>ファイル内の Orientation 値 2 バイトだけを上書きする。</summary>
    private static void WriteOrientationInPlace(string path, int valueOffset, bool bigEndian, int value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Position = valueOffset;
        Span<byte> buffer = stackalloc byte[2];
        if (bigEndian)
        {
            buffer[0] = (byte)(value >> 8);
            buffer[1] = (byte)value;
        }
        else
        {
            buffer[0] = (byte)value;
            buffer[1] = (byte)(value >> 8);
        }

        stream.Write(buffer);
    }

    /// <summary>Orientation だけを持つ最小の Exif APP1 を SOI 直後へ挿入して書き戻す。</summary>
    private static void InsertOrientationApp1(string path, byte[] bytes, int value)
    {
        // APP1 の中身: "Exif\0\0" + TIFF ヘッダー(8) + IFD エントリー数(2) + エントリー(12) + 次 IFD(4)
        var segment = new byte[]
        {
            0xFF, 0xE1, 0x00, 0x22,                         // APP1、長さ = 34（長さ自身の 2 バイトを含む）
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,             // "Exif\0\0"
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // TIFF ヘッダー（リトルエンディアン、IFD0 は +8）
            0x01, 0x00,                                     // IFD0 のエントリー数 = 1
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, // タグ 0x0112 / SHORT / 個数 1
            (byte)value, 0x00, 0x00, 0x00,                  // 値（SHORT は先頭 2 バイト）
            0x00, 0x00, 0x00, 0x00,                         // 次 IFD なし
        };

        var output = new byte[bytes.Length + segment.Length];
        // SOI（2 バイト）→ 差し込む APP1 → 元のデータ本体、の順に並べ替えるだけ。
        Buffer.BlockCopy(bytes, 0, output, 0, 2);
        Buffer.BlockCopy(segment, 0, output, 2, segment.Length);
        Buffer.BlockCopy(bytes, 2, output, 2 + segment.Length, bytes.Length - 2);

        // ファイル全体を書き直す経路なので、途中で落ちても元画像が残るよう原子的に置き換える
        // （2 バイトだけ書き換える WriteOrientationInPlace と違い、直書きは失敗が即データ損失になる）。
        AtomicFile.WriteAllBytes(path, output);
    }

    /// <summary>Exif Orientation の位置と現在値。</summary>
    private readonly record struct OrientationLocation(int ValueOffset, bool BigEndian, int Value);

    private static bool IsJpeg(byte[] bytes)
        => bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private static bool IsTiff(byte[] bytes)
        => bytes.Length >= 8
           && ((bytes[0] == 0x49 && bytes[1] == 0x49) || (bytes[0] == 0x4D && bytes[1] == 0x4D));

    /// <summary>Exif の APP1 セグメントが存在するか（挿入して良いかの判定用）。</summary>
    private static bool HasExifSegment(byte[] bytes)
        => TryFindTiffHeader(bytes, out _);

    /// <summary>Orientation タグの位置を探す。</summary>
    private static bool TryLocate(byte[] bytes, out OrientationLocation location)
    {
        location = default;
        return TryFindTiffHeader(bytes, out var tiffStart) && TryReadOrientationEntry(bytes, tiffStart, out location);
    }

    /// <summary>TIFF ヘッダー（Exif 本体）の先頭位置を求める。</summary>
    private static bool TryFindTiffHeader(byte[] bytes, out int tiffStart)
    {
        tiffStart = 0;
        if (IsTiff(bytes))
        {
            return true;
        }

        if (!IsJpeg(bytes))
        {
            return false;
        }

        // JPEG のマーカーを SOS まで順に辿り、Exif の APP1 を探す。
        var pos = 2;
        while (pos + 4 <= bytes.Length)
        {
            if (bytes[pos] != 0xFF)
            {
                return false;
            }

            var marker = bytes[pos + 1];

            // 埋め草の 0xFF。次のバイトからマーカーを読み直す。
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }

            // 長さを持たない単独マーカー。
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8)
            {
                pos += 2;
                continue;
            }

            // SOS 以降は圧縮データなので、そこまでに見つからなければ Exif は無い。
            if (marker is 0xDA or 0xD9)
            {
                return false;
            }

            var length = (bytes[pos + 2] << 8) | bytes[pos + 3];
            if (length < 2 || pos + 2 + length > bytes.Length)
            {
                return false;
            }

            if (marker == 0xE1 && length >= 8 + 6
                && bytes[pos + 4] == 0x45 && bytes[pos + 5] == 0x78 && bytes[pos + 6] == 0x69
                && bytes[pos + 7] == 0x66 && bytes[pos + 8] == 0x00 && bytes[pos + 9] == 0x00)
            {
                tiffStart = pos + 10;
                return tiffStart + 8 <= bytes.Length;
            }

            pos += 2 + length;
        }

        return false;
    }

    /// <summary>TIFF の IFD0 から Orientation エントリーを探す。</summary>
    private static bool TryReadOrientationEntry(byte[] bytes, int tiffStart, out OrientationLocation location)
    {
        location = default;
        if (tiffStart + 8 > bytes.Length)
        {
            return false;
        }

        var bigEndian = bytes[tiffStart] == 0x4D && bytes[tiffStart + 1] == 0x4D;
        var littleEndian = bytes[tiffStart] == 0x49 && bytes[tiffStart + 1] == 0x49;
        if (!bigEndian && !littleEndian)
        {
            return false;
        }

        if (ReadUInt16(bytes, tiffStart + 2, bigEndian) != 42)
        {
            return false;
        }

        var ifdOffset = ReadUInt32(bytes, tiffStart + 4, bigEndian);
        if (ifdOffset > int.MaxValue)
        {
            return false;
        }

        var ifdStart = tiffStart + (int)ifdOffset;
        if (ifdStart < tiffStart || ifdStart + 2 > bytes.Length)
        {
            return false;
        }

        var count = ReadUInt16(bytes, ifdStart, bigEndian);
        if (ifdStart + 2 + count * 12 > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var entry = ifdStart + 2 + (i * 12);
            if (ReadUInt16(bytes, entry, bigEndian) != OrientationTag)
            {
                continue;
            }

            if (ReadUInt16(bytes, entry + 2, bigEndian) != TypeShort)
            {
                return false;
            }

            // SHORT 1 個は値フィールドの先頭 2 バイトに直接入る（オフセット参照にはならない）。
            var valueOffset = entry + 8;
            var value = ReadUInt16(bytes, valueOffset, bigEndian);
            location = new OrientationLocation(valueOffset, bigEndian, value is >= 1 and <= 8 ? value : 1);
            return true;
        }

        return false;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset, bool bigEndian)
        => bigEndian
            ? (ushort)((bytes[offset] << 8) | bytes[offset + 1])
            : (ushort)((bytes[offset + 1] << 8) | bytes[offset]);

    private static uint ReadUInt32(byte[] bytes, int offset, bool bigEndian)
        => bigEndian
            ? ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]
            : ((uint)bytes[offset + 3] << 24) | ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 1] << 8) | bytes[offset];
}
