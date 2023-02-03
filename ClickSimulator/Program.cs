using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Linq;
using System.Runtime.InteropServices;
using CommandLine;
using System.Diagnostics;

namespace SimulateClick
{
    class Program
    {
        static uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        static uint MOUSEEVENTF_LEFTUP = 0x0004;
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow([In] IntPtr hWnd, [In] int nCmdShow);

        public class Options
        {
            [Option('x',"positionX",Required =false,Default =100,HelpText ="鼠标点击x坐标")]
            public int x { get; set; }

            [Option('y', "positionX", Required = false, Default = 100, HelpText = "鼠标点击y坐标")]
            public int y { get; set; }

            [Option('d', "delay", Required = false, Default = 1000, HelpText = "延迟执行ms")]
            public int d { get; set; }

            [Option('r',"repeat",Required =false,Default = 5,HelpText ="重复执行次数")]
            public int r { get; set; }

            [Option('i',"interval",Required =false,Default = 3000,HelpText ="重复间隔ms")]
            public int i { get; set; }
        }

        static void Main(string[] args)
        {
            IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
            ShowWindow(handle, 6);

            bool _exit = false;
            Parser
                .Default
                .ParseArguments<Options>(args)
                .WithParsed<Options>(options =>
                {
                    Observable.Interval(TimeSpan.FromMilliseconds(options.i))
                        .Take(options.r)
                        .Subscribe(_ =>
                        {
                            SetCursorPos(options.x, options.y);
                            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0,0, 0, 0);
                            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + $" simulated {_} times");
                        },()=> {
                            _exit = true;
                            Console.WriteLine("simulate click task completed.");
                            Environment.Exit(0);
                        });
                });
            while (!_exit) { }
        }
    }
}
