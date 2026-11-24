using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

using OmniRead.Localization;

namespace OmniRead;

/// <summary>What the status bar binds to for page numbers: the page being read, how many there are, and the jump command.</summary>
public sealed class PageNavigator : INotifyPropertyChanged {
    private int _currentPage;
    private int _totalPages;
    private string _currentChapterTitle = "";

    public PageNavigator(Func<int, Task> jump) {
        JumpToPageCommand = new JumpCommand(jump);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int CurrentPage {
        get => _currentPage;
        set => Set(ref _currentPage, value, nameof(CurrentPage));
    }

    public int TotalPages {
        get => _totalPages;
        set {
            if (Set(ref _totalPages, value, nameof(TotalPages))) {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPages)));
            }
        }
    }

    public string CurrentChapterTitle {
        get => _currentChapterTitle;
        set => Set(ref _currentChapterTitle, value, nameof(CurrentChapterTitle));
    }

    /// <summary>"of 412", in the reader's language.</summary>
    public string TotalText => Localizer.Format("Status_OfPages", _totalPages);

    public bool HasPages => _totalPages > 0;

    /// <summary>Goes to the page given as the command parameter, a number or text holding one.</summary>
    public ICommand JumpToPageCommand { get; }

    private bool Set<T>(ref T field, T value, string name) {
        if (EqualityComparer<T>.Default.Equals(field, value)) {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private sealed class JumpCommand(Func<int, Task> jump) : ICommand {
        public event EventHandler? CanExecuteChanged {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) {
            return true;
        }

        public void Execute(object? parameter) {
            int? page = parameter switch {
                int number => number,
                string text when int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) => parsed,
                _ => null
            };
            if (page is int target) {
                _ = jump(target);
            }
        }
    }
}