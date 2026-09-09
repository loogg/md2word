using System.Runtime.InteropServices;

namespace Md2Word.Worker.Infrastructure;

internal sealed class ComObjectTracker : IDisposable
{
    private readonly List<object> objects = [];

    public T Track<T>(T value) where T : class
    {
        if (Marshal.IsComObject(value))
        {
            objects.Add(value);
        }
        return value;
    }

    public void Dispose()
    {
        for (var index = objects.Count - 1; index >= 0; index--)
        {
            try
            {
                Marshal.FinalReleaseComObject(objects[index]);
            }
            catch (InvalidComObjectException)
            {
                // Already released by an earlier cleanup path.
            }
            catch (COMException)
            {
                // Word is already shutting down; cleanup must remain best effort.
            }
        }
        objects.Clear();
    }
}
