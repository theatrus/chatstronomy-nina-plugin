using System.ComponentModel.Composition;
using System.Windows;

namespace Chatstronomy.NINA;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }
}
