﻿/*
    Copyright 2015 MCGalaxy
        
    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at
    
    http://www.opensource.org/licenses/ecl2.php
    http://www.gnu.org/licenses/gpl-3.0.html
    
    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
 */
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace MCGalaxy.Drawing {
    
    public sealed class PixelGetter : IDisposable {
        
        Bitmap bmp;
        BitmapData data;
        public PixelGetter(Bitmap bmp) {
            this.bmp = bmp;
        }
        
        public void Init() {
            bool fastPath = bmp.PixelFormat == PixelFormat.Format32bppRgb
                || bmp.PixelFormat == PixelFormat.Format32bppArgb;
            if (!fastPath) return;
            // We can only use the fast path for 32bpp bitmaps
            
            Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
            data = bmp.LockBits(r, ImageLockMode.ReadOnly, bmp.PixelFormat);
        }
        
        public void Iterate(Action<Pixel> callback) {
            if (data == null) IterateSlow(callback);
            else IterateFast(callback);
        }
        
        unsafe void IterateFast(Action<Pixel> callback) {
            Pixel pixel;
            int width = bmp.Width, height = bmp.Height;
            byte* scan0 = (byte*)data.Scan0;
                
            for (int y = 0; y < height; y++) {
                pixel.Y = (ushort)y;
                int* row = (int*)(scan0 + y * data.Stride);
                for (int x = 0; x < width; x++) {
                    pixel.X = (ushort)x;
                    pixel.ARGB = row[x];
                    callback(pixel);
                }
            }
        }
        
        void IterateSlow(Action<Pixel> callback) {
            Pixel pixel;
            int width = bmp.Width, height = bmp.Height;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
            {
                pixel.X = (ushort)x; pixel.Y = (ushort)y;
                pixel.ARGB = bmp.GetPixel(x, y).ToArgb(); // R/G/B properties incur overhead
                callback(pixel);
            }
        }
        
        public void Dispose() {
            if (data != null) bmp.UnlockBits(data);
            data = null;
            bmp = null;
        }
    }
    
    public struct Pixel {
        public ushort X, Y;
        public int ARGB;
    }
}