using System;
using System.Windows;

namespace WpfStudio.PreviewFrameworkFixture
{
    /// <summary>Preview takes the resources from the build and must never construct this.</summary>
    public partial class App : Application
    {
        public App() => throw new InvalidOperationException("The preview ran the application's constructor.");
    }
}
