using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PlankWin
{
    // Класс-предохранитель. Отключает багованную систему UI Automation для списка иконок,
    // спасая WPF от вылетов (NullReferenceException) при Drag & Drop.
    public class DockItemsControl : ItemsControl
    {
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new FrameworkElementAutomationPeer(this);
        }
    }
}