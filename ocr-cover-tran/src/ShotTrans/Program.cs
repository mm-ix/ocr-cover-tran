using System.Runtime.InteropServices;

namespace ShotTrans;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--test-page", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TestPageForm());
            return;
        }

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run();
            return;
        }

        int stressIndex = Array.FindIndex(args, a => a.Equals("--stress", StringComparison.OrdinalIgnoreCase));
        if (stressIndex >= 0)
        {
            int cycles = 100;
            if (stressIndex + 1 < args.Length && int.TryParse(args[stressIndex + 1], out int n))
                cycles = n;
            StressTest.Run(cycles);
            return;
        }

        using var mutex = new Mutex(true, @"Local\ShotTrans.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ShotTrans 已在运行，请在系统托盘查看。", "ShotTrans",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        if (Environment.GetEnvironmentVariable("SHOTRANS_DEBUG_CAPTURE_OVERLAY") == "1")
            Windows.LayeredWindow.ExcludeFromCapture = false;

        Application.ThreadException += (_, e) =>
        {
            Log.Error("UI 线程未处理异常", e.Exception);
            MessageBox.Show("发生错误：" + e.Exception.Message + "\n详情已写入日志。", "ShotTrans",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("未处理异常", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未观察的异步异常", e.Exception);
            e.SetObserved();
        };

        using var app = new ShotTransApp(args);
        Application.Run(app);
    }
}
