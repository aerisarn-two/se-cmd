using LeanMeshIO.Formats.Fbx;
using LeanMeshIO;

namespace SECmd.Fbx
{
    /// <summary>
    /// How two FBX objects are related.
    /// </summary>
    public enum FbxConnectionKind
    {
        /// <summary>Object to object: the usual parent/child or owner/owned edge.</summary>
        ObjectObject,

        /// <summary>Object to property: an animation curve driving a named property.</summary>
        ObjectProperty
    }

    /// <summary>
    /// One edge of the FBX object graph.
    /// </summary>
    /// <remarks>
    /// FBX stores its scene as a flat pool of objects plus a list of connections,
    /// written child-first: <c>C: "OO", child, parent</c>. Nothing about the
    /// hierarchy is implied by the order of the objects themselves.
    /// </remarks>
    public sealed record FbxConnection(
        FbxConnectionKind Kind,
        long SourceId,
        long DestinationId,
        string PropertyName = "")
    {
        internal FbxNode ToNode()
        {
            var node = new FbxNode("C");
            node.Properties.Add(Kind == FbxConnectionKind.ObjectObject ? "OO" : "OP");
            node.Properties.Add(SourceId);
            node.Properties.Add(DestinationId);

            if (Kind == FbxConnectionKind.ObjectProperty)
                node.Properties.Add(PropertyName);

            return node;
        }
    }

    /// <summary>
    /// An object in the FBX object pool: a model, a mesh, a material, a deformer, an
    /// animation curve, and so on.
    /// </summary>
    /// <remarks>
    /// Wraps the underlying record rather than copying out of it, so that anything
    /// this project does not model — exporter-specific children, unusual properties
    /// — still survives being read and written back.
    /// </remarks>
    public sealed class FbxObject
    {
        private FbxProperties? _properties;

        internal FbxObject(FbxNode node) => Node = node;

        /// <summary>The underlying record, e.g. a <c>Model</c> or <c>Geometry</c> node.</summary>
        public FbxNode Node { get; }

        /// <summary>The record name, which is the object's class: Model, Geometry, Deformer, ...</summary>
        public string Class => Node.Name ?? string.Empty;

        /// <summary>The unique id other objects connect to.</summary>
        public long Id
        {
            get => Node.Properties.Count > 0 ? Convert.ToInt64(Node.Properties[0]) : 0;
            set
            {
                EnsureProperties(1);
                Node.Properties[0] = value;
            }
        }

        /// <summary>
        /// The full name as stored, which is <c>"Class::Name"</c>.
        /// </summary>
        public string QualifiedName
        {
            get => Node.Properties.Count > 1 ? Node.Properties[1] as string ?? string.Empty : string.Empty;
            set
            {
                EnsureProperties(2);
                Node.Properties[1] = value;
            }
        }

        /// <summary>The name with the <c>Class::</c> prefix stripped off.</summary>
        public string Name
        {
            get
            {
                string qualified = QualifiedName;
                int separator = qualified.IndexOf("::", StringComparison.Ordinal);
                return separator >= 0 ? qualified[(separator + 2)..] : qualified;
            }
        }

        /// <summary>
        /// The object's subclass: "Mesh" or "Null" for a Model, "Skin" or "Cluster"
        /// for a Deformer, and so on.
        /// </summary>
        public string SubClass
        {
            get => Node.Properties.Count > 2 ? Node.Properties[2] as string ?? string.Empty : string.Empty;
            set
            {
                EnsureProperties(3);
                Node.Properties[2] = value;
            }
        }

        /// <summary>The object's <c>Properties70</c> block, created on first use.</summary>
        public FbxProperties Properties
        {
            get
            {
                if (_properties is not null)
                    return _properties;

                FbxNode? block = Node.Nodes.FirstOrDefault(n => n.Name == "Properties70");

                if (block is null)
                {
                    block = new FbxNode("Properties70");
                    Node.Nodes.Add(block);
                }

                return _properties = new FbxProperties(block);
            }
        }

        /// <summary>The first child record with this name, or null.</summary>
        public FbxNode? Child(string name) => Node.Nodes.FirstOrDefault(n => n.Name == name);

        private void EnsureProperties(int count)
        {
            while (Node.Properties.Count < count)
                Node.Properties.Add(count == 2 ? string.Empty : 0L);
        }

        public override string ToString() => $"{Class} {Id} \"{Name}\" ({SubClass})";
    }

    /// <summary>
    /// The object graph of an FBX file: the pool of objects plus the connections
    /// between them.
    /// </summary>
    /// <remarks>
    /// This is the layer the conversion code works against. It stays close to the
    /// file — objects keep their records, connections keep their ids — because the
    /// FBX features this project needs are spread across object classes that no
    /// tidier abstraction would cover uniformly.
    /// </remarks>
    public sealed class FbxScene
    {
        private readonly FbxDocument _document;
        private readonly List<FbxObject> _objects = [];
        private readonly List<FbxConnection> _connections = [];
        private readonly Dictionary<long, FbxObject> _byId = [];

        public FbxScene(FbxDocument document)
        {
            _document = document;
            Read();
        }

        public FbxDocument Document => _document;

        public IReadOnlyList<FbxObject> Objects => _objects;

        public IReadOnlyList<FbxConnection> Connections => _connections;

        /// <summary>Looks an object up by the id that connections refer to.</summary>
        public FbxObject? this[long id] => _byId.GetValueOrDefault(id);

        /// <summary>Every object of a given record class, e.g. all Models.</summary>
        public IEnumerable<FbxObject> OfClass(string className) =>
            _objects.Where(o => o.Class == className);

        /// <summary>Every object of a class with a given subclass, e.g. Deformer/Cluster.</summary>
        public IEnumerable<FbxObject> OfClass(string className, string subClass) =>
            _objects.Where(o => o.Class == className && o.SubClass == subClass);

        private void Read()
        {
            FbxNode? objects = _document["Objects"];

            if (objects is not null)
            {
                foreach (FbxNode node in objects.Nodes)
                {
                    var o = new FbxObject(node);
                    _objects.Add(o);
                    _byId[o.Id] = o;
                }
            }

            FbxNode? connections = _document["Connections"];

            if (connections is null)
                return;

            foreach (FbxNode node in connections.Nodes)
            {
                if (node.Name != "C" || node.Properties.Count < 3)
                    continue;

                string kind = node.Properties[0] as string ?? "OO";
                long source = Convert.ToInt64(node.Properties[1]);
                long destination = Convert.ToInt64(node.Properties[2]);

                if (kind == "OP")
                {
                    string property = node.Properties.Count > 3 ? node.Properties[3] as string ?? string.Empty : string.Empty;
                    _connections.Add(new FbxConnection(FbxConnectionKind.ObjectProperty, source, destination, property));
                }
                else
                {
                    _connections.Add(new FbxConnection(FbxConnectionKind.ObjectObject, source, destination));
                }
            }
        }

        // --- graph queries ----------------------------------------------------

        /// <summary>
        /// The objects connected *into* <paramref name="id"/>, i.e. its children or
        /// the things it owns.
        /// </summary>
        public IEnumerable<FbxObject> ChildrenOf(long id)
        {
            foreach (FbxConnection c in _connections)
            {
                if (c.Kind == FbxConnectionKind.ObjectObject && c.DestinationId == id && _byId.TryGetValue(c.SourceId, out var o))
                    yield return o;
            }
        }

        /// <summary>The objects <paramref name="id"/> is connected to, i.e. its parents or owners.</summary>
        public IEnumerable<FbxObject> ParentsOf(long id)
        {
            foreach (FbxConnection c in _connections)
            {
                if (c.Kind == FbxConnectionKind.ObjectObject && c.SourceId == id && _byId.TryGetValue(c.DestinationId, out var o))
                    yield return o;
            }
        }

        /// <summary>
        /// The Models at the top of the hierarchy, i.e. those connected to the scene
        /// root, which FBX denotes with id 0.
        /// </summary>
        public IEnumerable<FbxObject> RootModels() =>
            _connections
                .Where(c => c.Kind == FbxConnectionKind.ObjectObject && c.DestinationId == 0)
                .Select(c => _byId.GetValueOrDefault(c.SourceId))
                .Where(o => o is { Class: "Model" })!
                .Select(o => o!);

        /// <summary>Objects driving a named property of <paramref name="id"/>.</summary>
        public IEnumerable<(FbxObject Source, string Property)> PropertyConnectionsTo(long id)
        {
            foreach (FbxConnection c in _connections)
            {
                if (c.Kind == FbxConnectionKind.ObjectProperty && c.DestinationId == id && _byId.TryGetValue(c.SourceId, out var o))
                    yield return (o, c.PropertyName);
            }
        }

        // --- mutation ---------------------------------------------------------

        /// <summary>
        /// Adds an object, assigning it a fresh id when it does not have one.
        /// </summary>
        public FbxObject AddObject(string className, string name, string subClass, long? id = null)
        {
            var node = new FbxNode(className);
            var o = new FbxObject(node);

            node.Properties.Add(id ?? NextId());
            node.Properties.Add($"{className}::{name}");
            node.Properties.Add(subClass);

            _objects.Add(o);
            _byId[o.Id] = o;
            return o;
        }

        public void Connect(FbxObject child, FbxObject parent) =>
            _connections.Add(new FbxConnection(FbxConnectionKind.ObjectObject, child.Id, parent.Id));

        /// <summary>Connects an object to the scene root, which FBX denotes with id 0.</summary>
        public void ConnectToRoot(FbxObject child) =>
            _connections.Add(new FbxConnection(FbxConnectionKind.ObjectObject, child.Id, 0));

        public void ConnectToProperty(FbxObject source, FbxObject destination, string propertyName) =>
            _connections.Add(new FbxConnection(FbxConnectionKind.ObjectProperty, source.Id, destination.Id, propertyName));

        private long _nextId = 1_000_000;

        /// <summary>An id not already in use.</summary>
        public long NextId()
        {
            while (_byId.ContainsKey(_nextId))
                _nextId++;

            return _nextId++;
        }

        /// <summary>
        /// Writes the objects and connections back into the underlying document,
        /// and refreshes the Definitions counts to match.
        /// </summary>
        public void Flush()
        {
            FbxNode objects = EnsureTopLevel("Objects");
            objects.Nodes.Clear();

            foreach (FbxObject o in _objects)
                objects.Nodes.Add(o.Node);

            FbxNode connections = EnsureTopLevel("Connections");
            connections.Nodes.Clear();

            foreach (FbxConnection c in _connections)
                connections.Nodes.Add(c.ToNode());

            UpdateDefinitions();
        }

        private FbxNode EnsureTopLevel(string name)
        {
            FbxNode? node = _document[name];

            if (node is not null)
                return node;

            node = new FbxNode(name);
            _document.Nodes.Add(node);
            return node;
        }

        /// <summary>
        /// Rewrites the Definitions block's total and per-class counts.
        /// </summary>
        /// <remarks>
        /// Importers use these to size their pools up front. A count that disagrees
        /// with the object list makes some of them drop objects silently, so this
        /// has to be kept in step with any edit.
        /// </remarks>
        private void UpdateDefinitions()
        {
            FbxNode definitions = EnsureTopLevel("Definitions");

            FbxNode? count = definitions.Nodes.FirstOrDefault(n => n.Name == "Count");

            if (count is null)
            {
                count = new FbxNode("Count");
                count.Properties.Add(0);
                definitions.Nodes.Add(count);
            }

            count.Properties[0] = _objects.Count;

            foreach (IGrouping<string, FbxObject> group in _objects.GroupBy(o => o.Class))
            {
                FbxNode? objectType = definitions.Nodes.FirstOrDefault(
                    n => n.Name == "ObjectType" && n.Properties.Count > 0 && n.Properties[0] as string == group.Key);

                if (objectType is null)
                {
                    objectType = new FbxNode("ObjectType");
                    objectType.Properties.Add(group.Key);
                    definitions.Nodes.Add(objectType);
                }

                FbxNode? typeCount = objectType.Nodes.FirstOrDefault(n => n.Name == "Count");

                if (typeCount is null)
                {
                    typeCount = new FbxNode("Count");
                    typeCount.Properties.Add(0);
                    objectType.Nodes.Add(typeCount);
                }

                typeCount.Properties[0] = group.Count();
            }
        }
    }
}
