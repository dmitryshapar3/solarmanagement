namespace DeyeSolar.Web.Components.Ui;
public sealed class UiFormState
{
    private readonly HashSet<string> _invalid = [];
    public bool IsValid => _invalid.Count == 0;
    public event Action? Changed;
    public void Mark(string id, bool valid) { if(valid?_invalid.Remove(id):_invalid.Add(id))Changed?.Invoke(); }
    public void Remove(string id) { if(_invalid.Remove(id))Changed?.Invoke(); }
}
