using PSDToolViewer.Services;
using Velopack;

namespace PSDToolViewer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // インストール・更新・アンインストール時のフック。該当する場合はここで処理して終了する
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => FileAssociation.Register())
            .OnAfterUpdateFastCallback(_ => FileAssociation.Register())
            .OnBeforeUninstallFastCallback(_ => FileAssociation.Unregister())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
