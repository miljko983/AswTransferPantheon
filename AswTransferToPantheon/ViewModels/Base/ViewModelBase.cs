using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AswTransferToPantheon.ViewModels.Base
{
    public class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected static void HandleError(Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected virtual void SetProperty<T>(ref T property, T value, [CallerMemberName] string? propertyName = null)
        {
            property = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
