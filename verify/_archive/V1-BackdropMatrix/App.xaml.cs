using System;
using System.Windows;

namespace V1BackdropMatrix;

public partial class App : Application
{
    // 注意：不自己写 Main()。App.xaml 存在时 WPF 的构建目标会自动生成一个 Main
    // （App.g.cs），再手写一个就会撞成 CS0111。入口逻辑改写在 OnStartup 里。
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MatrixWindow().Show();
    }
}
