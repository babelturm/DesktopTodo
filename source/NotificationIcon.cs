using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace DesktopTodo
{
    static class NotificationIcon
    {
        public static Bitmap Create(int size)
        {
            const int supersample=4;
            using(var large=new Bitmap(size*supersample,size*supersample,PixelFormat.Format32bppArgb))
            {
                using(var g=Graphics.FromImage(large))
                {
                    g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.ScaleTransform(size*supersample/48F,size*supersample/48F);
                    g.TranslateTransform(6,25); g.RotateTransform(-45);
                    // Both arms are exactly eight units wide. Their shared lower
                    // edge is y=18; one union outline prevents overlap protrusions.
                    using(var outline=RoundedPolygon(new[]{new PointF(0,0),new PointF(8,0),new PointF(8,10),new PointF(32,10),new PointF(32,18),new PointF(0,18)},.8F))
                    {
                        using(var light=new SolidBrush(Color.FromArgb(57,164,239))) g.FillPath(light,outline);
                        g.SetClip(outline,CombineMode.Intersect);
                        using(var dark=new SolidBrush(Color.FromArgb(24,90,189))) g.FillRectangle(dark,0,0,8,10);
                    }
                }
                var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(bitmap)) { g.Clear(Color.Transparent); g.CompositingMode=CompositingMode.SourceCopy; g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.DrawImage(large,new Rectangle(0,0,size,size),0,0,large.Width,large.Height,GraphicsUnit.Pixel); }
                return bitmap;
            }
        }
        public static void WriteIco(string path)
        { File.WriteAllBytes(path,BuildIco(new[]{16,20,24,32,40,48,64,128,256})); }
        public static Icon CreateIcon(int size)
        { using(var stream=new MemoryStream(BuildIco(new[]{size}))) using(var icon=new Icon(stream,size,size)) return (Icon)icon.Clone(); }
        static byte[] BuildIco(int[] sizes)
        {
            var frames=new List<byte[]>();
            foreach(int size in sizes) using(var image=Create(size)) using(var stream=new MemoryStream())
            {
                if(size==256) image.Save(stream,ImageFormat.Png);
                else using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
                {
                    int maskStride=((size+31)/32)*4;
                    writer.Write(40); writer.Write(size); writer.Write(size*2); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(size*size*4+maskStride*size); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    for(int y=size-1;y>=0;y--) for(int x=0;x<size;x++) { var pixel=image.GetPixel(x,y); writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A); }
                    for(int y=size-1;y>=0;y--) { var mask=new byte[maskStride]; for(int x=0;x<size;x++) if(image.GetPixel(x,y).A==0) mask[x/8]|=(byte)(0x80>>(x%8)); writer.Write(mask); }
                }
                frames.Add(stream.ToArray());
            }
            using(var stream=new MemoryStream())
            {
                using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
                {
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
                    for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i]==256 ? 0 : sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset+=frames[i].Length; }
                    foreach(var frame in frames) writer.Write(frame);
                }
                return stream.ToArray();
            }
        }
        static GraphicsPath RoundedPolygon(PointF[] points,float radius)
        {
            var path=new GraphicsPath(); PointF first=PointF.Empty,previous=PointF.Empty;
            for(int i=0;i<points.Length;i++)
            {
                PointF vertex=points[i],before=points[(i+points.Length-1)%points.Length],after=points[(i+1)%points.Length];
                var entry=Towards(vertex,before,radius); var exit=Towards(vertex,after,radius);
                if(i==0) { first=entry; path.StartFigure(); } else path.AddLine(previous,entry);
                path.AddBezier(entry,vertex,vertex,exit); previous=exit;
            }
            path.AddLine(previous,first); path.CloseFigure(); return path;
        }
        static PointF Towards(PointF from,PointF to,float distance)
        { float dx=to.X-from.X,dy=to.Y-from.Y,length=(float)Math.Sqrt(dx*dx+dy*dy); return new PointF(from.X+dx*distance/length,from.Y+dy*distance/length); }
        public static string Write(string directory)
        {
            // Use a new URI so Windows does not reuse its cached checklist icon.
            string path=Path.Combine(directory,"notification-icon-blue-check-v2.png");
            using(var icon=Create(128)) { icon.Save(path,ImageFormat.Png); icon.Save(Path.Combine(directory,"notification-icon.png"),ImageFormat.Png); }
            return path;
        }
    }
}
