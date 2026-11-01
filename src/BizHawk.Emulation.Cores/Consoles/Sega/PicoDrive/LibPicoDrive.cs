using System.Runtime.InteropServices;

using BizHawk.BizInvoke;
using BizHawk.Emulation.Cores.Waterbox;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	public abstract class LibPicoDrive : LibWaterboxCore
	{
		[StructLayout(LayoutKind.Sequential)]
		public new class FrameInfo : LibWaterboxCore.FrameInfo
		{
			public int Buttons;

			// XE-1AP analog values, 0..255 with 128 = center
			public int P1StickX;
			public int P1StickY;
			public int P1Throttle;
			public int P2StickX;
			public int P2StickY;
			public int P2Throttle;
		}

		public enum ControllerType : int
		{
			SixButton = 0,
			XE1AP = 1,
		}

		[UnmanagedFunctionPointer(CC)]
		public delegate void CDReadCallback(int lba, IntPtr dest, bool audio);

		public enum Region : int
		{
			Auto = 0,
			JapanNTSC = 1,
			JapanPAL = 2,
			US = 4,
			Europe = 8
		}

		/// <param name="cd">If TRUE, load a CD and not a cart.</param>
		/// <param name="port1Device">device plugged into port 1</param>
		/// <param name="port2Device">device plugged into port 2</param>
		/// <param name="_32xPreinit">If TRUE, preallocate 32X data structures.  When set to false,
		///		32X games will still run, but will not have memory domains</param>
		[BizImport(CC)]
		public abstract bool Init(bool cd, bool _32xPreinit, Region regionAutoOrder, Region regionOverride, ControllerType port1Device, ControllerType port2Device);

		public const int CD_MAX_TRACKS = 100;

		[StructLayout(LayoutKind.Sequential)]
		public struct Track
		{
			public int start;
			public int end;
		}

		[StructLayout(LayoutKind.Sequential)]
		public class TOC
		{
			public int end;
			public int last;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = CD_MAX_TRACKS)]
			public readonly Track[] tracks = new Track[CD_MAX_TRACKS];
		}

		[BizImport(CC)]
		public abstract void SetCDReadCallback(CDReadCallback callback);

		[BizImport(CC)]
		public abstract bool IsPal();

		[BizImport(CC)]
		public abstract bool Is32xActive();
	}
}
