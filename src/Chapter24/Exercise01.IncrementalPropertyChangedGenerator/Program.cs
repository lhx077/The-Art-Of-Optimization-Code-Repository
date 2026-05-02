// ============================================================================
// Exercise 01 — Incremental PropertyChanged Generator
// ============================================================================
// Demonstrates a real IIncrementalGenerator that discovers [AutoNotify] classes
// at compile time and emits INotifyPropertyChanged property implementations.
// ============================================================================

using System.ComponentModel;

var person = new Person();
person.PropertyChanged += (_, e) => Console.WriteLine($"Changed: {e.PropertyName}");
person.Name = "Alice";
person.Age = 30;
Console.WriteLine($"Person: {person.Name}, age {person.Age}");

[AutoNotify]
public partial class Person : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private int _age;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
