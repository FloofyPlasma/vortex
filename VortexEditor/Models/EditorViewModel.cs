using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VortexEngine;

namespace VortexEditor.Models;

public sealed class EditorViewModel : INotifyPropertyChanged
{
    private readonly Engine engine;
    
    public Engine Engine => engine;

    public EditorViewModel(Engine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}