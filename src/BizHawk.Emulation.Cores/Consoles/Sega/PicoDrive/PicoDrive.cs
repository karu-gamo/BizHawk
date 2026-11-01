using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Waterbox;
using BizHawk.Emulation.DiscSystem;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	[PortedCore(CoreNames.PicoDrive, "notaz", "0e35290", "https://github.com/notaz/picodrive")]
	public class PicoDrive : WaterboxCore, IDriveLight, IRegionable, ISettable<object, PicoDrive.SyncSettings>
	{
		private readonly LibPicoDrive _core;
		private readonly LibPicoDrive.CDReadCallback _cdcallback;
		private readonly Disc _cd;
		private readonly DiscSectorReader _cdReader;
		private readonly bool _isPal;

		[CoreConstructor(VSystemID.Raw.Sega32X)]
		public PicoDrive(CoreComm comm, GameInfo game, byte[] rom, bool deterministic, SyncSettings syncSettings)
			: this(comm, game, rom, null, deterministic, syncSettings)
		{ }

		private PicoDrive(CoreComm comm, GameInfo game, byte[] rom, Disc cd, bool deterministic, SyncSettings syncSettings)
			: base(comm, new Configuration
			{
				MaxSamples = 2048,
				DefaultWidth = 320,
				DefaultHeight = 224,
				MaxWidth = 320,
				MaxHeight = 480,
				SystemId = game.System,
			})
		{
			var biosg = comm.CoreFileProvider.GetFirmware(new("32X", "G"));
			var biosm = comm.CoreFileProvider.GetFirmware(new("32X", "M"));
			var bioss = comm.CoreFileProvider.GetFirmware(new("32X", "S"));
			var has32xBios = biosg != null && biosm != null && bioss != null;
			if (deterministic && !has32xBios)
				throw new InvalidOperationException("32X BIOS files are required for deterministic mode");
			deterministic |= has32xBios;

			_syncSettings = syncSettings ?? new SyncSettings();

			_cdcallback = CDRead;

			_core = PreInit<LibPicoDrive>(new WaterboxOptions
			{
				Filename = "picodrive.wbx",
				SbrkHeapSizeKB = 64,
				SealedHeapSizeKB = 18 * 1024,
				InvisibleHeapSizeKB = 1024,
				MmapHeapSizeKB = 4096,
				PlainHeapSizeKB = 64,
				SkipCoreConsistencyCheck = comm.CorePreferences.HasFlag(CoreComm.CorePreferencesFlags.WaterboxCoreConsistencyCheck),
				SkipMemoryConsistencyCheck = comm.CorePreferences.HasFlag(CoreComm.CorePreferencesFlags.WaterboxMemoryConsistencyCheck),
			}, new Delegate[] { _cdcallback });

			if (has32xBios)
			{
				_exe.AddReadonlyFile(biosg, "32x.g");
				_exe.AddReadonlyFile(biosm, "32x.m");
				_exe.AddReadonlyFile(bioss, "32x.s");
				Console.WriteLine("Using supplied 32x BIOS files");
			}
			if (cd != null)
			{
				_exe.AddReadonlyFile(comm.CoreFileProvider.GetFirmwareOrThrow(new("GEN", "CD_BIOS_EU")), "cd.eu");
				_exe.AddReadonlyFile(comm.CoreFileProvider.GetFirmwareOrThrow(new("GEN", "CD_BIOS_US")), "cd.us");
				_exe.AddReadonlyFile(comm.CoreFileProvider.GetFirmwareOrThrow(new("GEN", "CD_BIOS_JP")), "cd.jp");
				_exe.AddReadonlyFile(GetTOC(cd), "toc");
				_cd = cd;
				_cdReader = new DiscSectorReader(_cd);
				_core.SetCDReadCallback(_cdcallback);
				DriveLightEnabled = true;
			}
			else
			{
				_exe.AddReadonlyFile(rom, "romfile.md");
			}

			var regionAutoOrder = (LibPicoDrive.Region)(
				(int)_syncSettings.FirstChoice |
				(int)_syncSettings.SecondChoice << 4 |
				(int)_syncSettings.ThirdChoice << 8);

			if (!_core.Init(cd != null, game["32X"], regionAutoOrder, _syncSettings.RegionOverride, _syncSettings.Port1, _syncSettings.Port2))
				throw new InvalidOperationException("Core rejected the file!");

			if (cd != null)
			{
				_exe.RemoveReadonlyFile("cd.eu");
				_exe.RemoveReadonlyFile("cd.us");
				_exe.RemoveReadonlyFile("cd.jp");
				_exe.RemoveReadonlyFile("toc");
				_core.SetCDReadCallback(null);
			}
			else
			{
				_exe.RemoveReadonlyFile("romfile.md");
			}
			if (has32xBios)
			{
				_exe.RemoveReadonlyFile("32x.g");
				_exe.RemoveReadonlyFile("32x.m");
				_exe.RemoveReadonlyFile("32x.s");
			}

			PostInit();
			_portTypes = new[] { _syncSettings.Port1, _syncSettings.Port2 };
			ControllerDefinition = MakeControllerDefinition(_portTypes);
			DeterministicEmulation = deterministic;
			_core.SetCDReadCallback(_cdcallback);

			_isPal = _core.IsPal();
			VsyncNumerator = _isPal ? 53203424 : 53693175;
			VsyncDenominator = _isPal ? 3420 * 313 : 3420 * 262;

			Is32XActive = game.System == VSystemID.Raw.Sega32X;
		}

		public bool Is32XActive { get; }

		public static readonly ControllerDefinition PicoDriveController = new ControllerDefinition("PicoDrive Genesis Controller")
		{
			BoolButtons =
			{
				"P1 Up", "P1 Down", "P1 Left", "P1 Right", "P1 A", "P1 B", "P1 C", "P1 Start", "P1 X", "P1 Y", "P1 Z", "P1 Mode",
				"P2 Up", "P2 Down", "P2 Left", "P2 Right", "P2 A", "P2 B", "P2 C", "P2 Start", "P2 X", "P2 Y", "P2 Z", "P2 Mode",
				"Power", "Reset"
			}
		}.MakeImmutable();

		// bit order of each port's 12 bit field in FrameInfo.Buttons
		private static readonly string[] SixButtonOrder =
		{
			"Up", "Down", "Left", "Right", "B", "C", "A", "Start", "Z", "Y", "X", "Mode"
		};

		// XE-1AP: must match the layout read_xe_1ap() expects in waterbox/picodrive/pico/memory.c
		private static readonly string[] Xe1apOrder =
		{
			"XE D", "XE C", "XE B2", "XE A2", "XE B", "XE A", "XE Select", "XE Start", "XE E2", "XE E1"
		};

		private readonly LibPicoDrive.ControllerType[] _portTypes;

		private static ControllerDefinition MakeControllerDefinition(LibPicoDrive.ControllerType[] ports)
		{
			if (ports.All(p => p == LibPicoDrive.ControllerType.SixButton))
				return PicoDriveController;

			var def = new ControllerDefinition("PicoDrive Genesis Controller (XE-1AP)");
			for (var i = 0; i < 2; i++)
			{
				var order = ports[i] == LibPicoDrive.ControllerType.XE1AP ? Xe1apOrder : SixButtonOrder;
				foreach (var s in order)
					def.BoolButtons.Add($"P{i + 1} {s}");
			}
			def.BoolButtons.Add("Power");
			def.BoolButtons.Add("Reset");
			for (var i = 0; i < 2; i++)
			{
				if (ports[i] == LibPicoDrive.ControllerType.XE1AP)
					def.AddXYZTriple($"P{i + 1} Stick {{0}}", (-128).RangeTo(127), 0);
			}
			return def.MakeImmutable();
		}

		private static int ToXeAnalog(int v) => Math.Min(Math.Max(v + 128, 0), 255);

		protected override LibWaterboxCore.FrameInfo FrameAdvancePrep(IController controller, bool render, bool rendersound)
		{
			var b = 0;
			for (var i = 0; i < 2; i++)
			{
				var order = _portTypes[i] == LibPicoDrive.ControllerType.XE1AP ? Xe1apOrder : SixButtonOrder;
				for (var j = 0; j < order.Length; j++)
				{
					if (controller.IsPressed($"P{i + 1} {order[j]}"))
						b |= 1 << (i * 12 + j);
				}
			}
			if (controller.IsPressed("Power"))
				b |= 1 << 24;
			if (controller.IsPressed("Reset"))
				b |= 1 << 25;

			var f = new LibPicoDrive.FrameInfo { Buttons = b };
			// XE-1AP: X 0 = left, throttle 255 = forward; Y is passed through as-is
			// (Y sign confirmed by a user in After Burner Complete menus)
			if (_portTypes[0] == LibPicoDrive.ControllerType.XE1AP)
			{
				f.P1StickX = ToXeAnalog(controller.AxisValue("P1 Stick X"));
				f.P1StickY = ToXeAnalog(controller.AxisValue("P1 Stick Y"));
				f.P1Throttle = ToXeAnalog(controller.AxisValue("P1 Stick Z"));
			}
			if (_portTypes[1] == LibPicoDrive.ControllerType.XE1AP)
			{
				f.P2StickX = ToXeAnalog(controller.AxisValue("P2 Stick X"));
				f.P2StickY = ToXeAnalog(controller.AxisValue("P2 Stick Y"));
				f.P2Throttle = ToXeAnalog(controller.AxisValue("P2 Stick Z"));
			}
			DriveLightOn = false;
			return f;
		}

		private static byte[] GetTOC(Disc cd)
		{
			var toc = new LibPicoDrive.TOC();

			var ses = cd.Session1;
			var ntrack = ses.InformationTrackCount;

			for (var i = 0; i < LibPicoDrive.CD_MAX_TRACKS; i++)
			{
				if (i < ntrack)
				{
					toc.tracks[i].start = ses.Tracks[i + 1].LBA;
					toc.tracks[i].end = ses.Tracks[i + 2].LBA;
					if (i == ntrack - 1)
					{
						toc.end = toc.tracks[i].end;
						toc.last = ntrack;
					}
				}
				else
				{
					toc.tracks[i].start = 0;
					toc.tracks[i].end = 0;
				}
			}

			var size = Marshal.SizeOf(toc);
			var ret = new byte[size];

			unsafe
			{
				fixed (byte* p = ret)
				{
					Marshal.StructureToPtr(ret, (IntPtr)p, false);
				}
			}

			return ret;
		}

		private void CDRead(int lba, IntPtr dest, bool audio)
		{
			if (audio)
			{
				byte[] data = new byte[2352];
				if (lba < _cd.Session1.LeadoutLBA && lba >= _cd.Session1.Tracks[2].LBA)
				{
					_cdReader.ReadLBA_2352(lba, data, 0);
				}
				Marshal.Copy(data, 0, dest, 2352);
			}
			else
			{
				byte[] data = new byte[2048];
				_cdReader.ReadLBA_2048(lba, data, 0);
				Marshal.Copy(data, 0, dest, 2048);
				DriveLightOn = true;
			}
		}

		protected override void LoadStateBinaryInternal(BinaryReader reader)
		{
			_core.SetCDReadCallback(_cdcallback);
		}

		[CoreSettings]
		public class SyncSettings
		{
			[DefaultValue(LibPicoDrive.Region.Auto)]
			[Description("If set, force the console to this region")]
			public LibPicoDrive.Region RegionOverride { get; set; }

			[DefaultValue(LibPicoDrive.Region.Auto)]
			[Description("When region is set to automatic, highest priority region to use if the game supports multiple regions")]
			public LibPicoDrive.Region FirstChoice { get; set; }

			[DefaultValue(LibPicoDrive.Region.Auto)]
			[Description("When region is set to automatic, second highest priority region to use if the game supports multiple regions")]
			public LibPicoDrive.Region SecondChoice { get; set; }

			[DefaultValue(LibPicoDrive.Region.Auto)]
			[Description("When region is set to automatic, lowest priority region to use if the game supports multiple regions")]
			public LibPicoDrive.Region ThirdChoice { get; set; }

			[DisplayName("Port 1 Device")]
			[DefaultValue(LibPicoDrive.ControllerType.SixButton)]
			[Description("Controller plugged into port 1. XE1AP = Dempa/Micomsoft XE-1AP analog pad (After Burner Complete, Space Harrier 32X, After Burner II, etc.)")]
			public LibPicoDrive.ControllerType Port1 { get; set; }

			[DisplayName("Port 2 Device")]
			[DefaultValue(LibPicoDrive.ControllerType.SixButton)]
			[Description("Controller plugged into port 2.")]
			public LibPicoDrive.ControllerType Port2 { get; set; }

			public SyncSettings Clone()
			{
				return (SyncSettings)MemberwiseClone();
			}

			public static bool NeedsReboot(SyncSettings x, SyncSettings y)
			{
				return !DeepEquality.DeepEquals(x, y);
			}

			public SyncSettings()
			{
				SettingsUtil.SetDefaultValues(this);
			}
		}

		private SyncSettings _syncSettings;

		public object GetSettings()
		{
			return new object();
		}

		public SyncSettings GetSyncSettings()
		{
			return _syncSettings.Clone();
		}

		public PutSettingsDirtyBits PutSettings(object o)
		{
			return PutSettingsDirtyBits.None;
		}

		public PutSettingsDirtyBits PutSyncSettings(SyncSettings o)
		{
			var ret = SyncSettings.NeedsReboot(_syncSettings, o);
			_syncSettings = o;
			return ret ? PutSettingsDirtyBits.RebootCore : PutSettingsDirtyBits.None;
		}

		public bool DriveLightEnabled { get; }
		public bool DriveLightOn { get; private set; }

		public string DriveLightIconDescription => "CD Drive Activity";

		public DisplayType Region => _isPal ? DisplayType.PAL : DisplayType.NTSC;
	}
}
