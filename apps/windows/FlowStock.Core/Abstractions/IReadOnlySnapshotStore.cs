namespace FlowStock.Core.Abstractions;

public interface IReadOnlySnapshotStore
{
    T ReadSnapshot<T>(Func<IDataStore, T> read);
}
