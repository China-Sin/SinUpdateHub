# 生成 SinUpdateHub 应用图标 (src\SinUpdateHub.App\Assets\app.ico)
# 深色紫灰风：紫色渐变圆角方块 + 白色 S + 右上角紫色向上箭头（更新）
param([string]$PreviewPath = '')
$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot '..\src\SinUpdateHub.App\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = [System.IO.Path]::GetFullPath((Join-Path $outDir 'app.ico'))

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.IO;

public static class IconMaker
{
    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float pad = size * 0.028f;
            float r = size * 0.24f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using (var path = Rounded(rect.X, rect.Y, rect.Width, rect.Height, r))
            {
                using (var brush = new LinearGradientBrush(rect,
                    Color.FromArgb(255, 104, 56, 190), Color.FromArgb(255, 22, 14, 38), 45f))
                {
                    g.FillPath(brush, path);
                }
                // 顶部高光
                g.SetClip(path);
                var hlRect = new RectangleF(0, 0, size, size * 0.62f);
                using (var hl = new LinearGradientBrush(hlRect,
                    Color.FromArgb(64, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    g.FillRectangle(hl, 0, 0, size, size);
                }
                g.ResetClip();
                using (var pen = new Pen(Color.FromArgb(230, 175, 130, 255), Math.Max(1f, size * 0.016f)))
                {
                    g.DrawPath(pen, path);
                }
            }

            // 字母 S
            float fs = size * 0.58f;
            using (var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.FromArgb(255, 243, 238, 252)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("S", font, white, new RectangleF(0, -size * 0.075f, size, size), fmt);
            }

            // 右上角向上箭头
            float cx = size * 0.765f, cy = size * 0.235f, aw = size * 0.195f;
            var pts = new[]
            {
                new PointF(cx, cy - aw * 0.62f),
                new PointF(cx - aw * 0.62f, cy + aw * 0.04f),
                new PointF(cx - aw * 0.24f, cy + aw * 0.04f),
                new PointF(cx - aw * 0.24f, cy + aw * 0.62f),
                new PointF(cx + aw * 0.24f, cy + aw * 0.62f),
                new PointF(cx + aw * 0.24f, cy + aw * 0.04f),
                new PointF(cx + aw * 0.62f, cy + aw * 0.04f),
            };
            using (var arrow = new GraphicsPath())
            using (var accent = new SolidBrush(Color.FromArgb(255, 190, 150, 255)))
            {
                arrow.AddPolygon(pts);
                g.FillPath(accent, arrow);
            }
        }
        return bmp;
    }

    public static void Build(string icoPath, string previewPath)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }
        if (!string.IsNullOrEmpty(previewPath))
        {
            File.WriteAllBytes(previewPath, pngs[pngs.Count - 1]);
        }
        using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                bw.Write(dim);
                bw.Write(dim);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)pngs[i].Length);
                bw.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }
    }
}
'@

$previewPath = $PreviewPath
if ($previewPath -ne '') {
    New-Item -ItemType Directory -Force -Path (Split-Path $previewPath) | Out-Null
}

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing
[IconMaker]::Build($icoPath, $previewPath)
Write-Host "OK -> $icoPath ($((Get-Item $icoPath).Length) bytes)"
if ($previewPath -ne '') { Write-Host "preview -> $previewPath" }
