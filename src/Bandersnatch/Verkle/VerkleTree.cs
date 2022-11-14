using System.Diagnostics;
using System.Runtime.CompilerServices;
using Curve;
using Field;

namespace Verkle;
using Fr = FixedFiniteField<BandersnatchScalarFieldStruct>;

public class VerkleTree
{
    private readonly MemoryDb _db;
    public byte[] RootHash => _db.BranchTable[Array.Empty<byte>()]._internalCommitment.PointAsField.ToBytes();

    public VerkleTree()
    {
        _db = new MemoryDb
        {
            BranchTable =
            {
                [Array.Empty<byte>()] = new BranchNode()
            }
        };
    }

    private static Banderwagon GetLeafDelta(byte[]? oldValue, byte[] newValue, byte index)
    {
        (Fr newValLow, Fr newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);
        (Fr oldValLow, Fr oldValHigh) = VerkleUtils.BreakValueInLowHigh(oldValue);

        int posMod128 = index % 128;
        int lowIndex = 2 * posMod128;
        int highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow - oldValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh - oldValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    public void Insert(Span<byte> key, byte[] value)
    {
        LeafUpdateDelta leafDelta = UpdateLeaf(key, value);
        UpdateTreeCommitments(key[..31], leafDelta);
    }

    public byte[]? Get(byte[] key) => _db.LeafTable.TryGetValue(key, out byte[]? leaf) ? leaf : null;

    public void InsertStemBatch(Span<byte> stem, Dictionary<byte, byte[]> leafIndexValueMap)
    {
        LeafUpdateDelta leafDelta = UpdateLeaf(stem, leafIndexValueMap);
        UpdateTreeCommitments(stem, leafDelta);
    }

    private void UpdateTreeCommitments(Span<byte> stem, LeafUpdateDelta leafUpdateDelta)
    {
        // calculate this by update the leafs and calculating the delta - simple enough
        TraverseContext context = new TraverseContext(stem, leafUpdateDelta);
        Banderwagon rootDelta = TraverseBranch(context);
        UpdateRootNode(rootDelta);
    }

    private void UpdateRootNode(Banderwagon rootDelta)
    {
        _db.BranchTable.TryGetValue(Array.Empty<byte>(), out InternalNode? root);
        Debug.Assert(root != null, nameof(root) + " != null");
        root._internalCommitment.AddPoint(rootDelta);
    }

    private Banderwagon TraverseBranch(TraverseContext traverseContext)
    {
        byte pathIndex = traverseContext.Stem[traverseContext.CurrentIndex];
        byte[] absolutePath = traverseContext.Stem[..(traverseContext.CurrentIndex + 1)].ToArray();

        InternalNode? child = GetBranchChild(absolutePath);
        if (child is null)
        {
            // 1. create new suffix node
            // 2. update the C1 or C2 - we already know the leafDelta - traverseContext.LeafUpdateDelta
            // 3. update ExtensionCommitment
            // 4. get the delta for commitment - ExtensionCommitment - 0;
            (Fr deltaHash, Commitment? suffixCommitment) = UpdateSuffixNode(traverseContext.Stem.ToArray(), traverseContext.LeafUpdateDelta, true);

            // 1. Add internal.stem node
            // 2. return delta from ExtensionCommitment
            Banderwagon point = Committer.ScalarMul(deltaHash, pathIndex);
            _db.BranchTable[absolutePath] = new StemNode(traverseContext.Stem.ToArray(), suffixCommitment!);
            return point;
        }

        Banderwagon parentDeltaHash;
        Banderwagon deltaPoint;
        if (child.IsBranchNode)
        {
            traverseContext.CurrentIndex += 1;
            parentDeltaHash = TraverseBranch(traverseContext);
            traverseContext.CurrentIndex -= 1;
            Fr deltaHash = child.UpdateCommitment(parentDeltaHash);
            _db.BranchTable[absolutePath] = child;
            deltaPoint = Committer.ScalarMul(deltaHash, pathIndex);
        }
        else
        {
            traverseContext.CurrentIndex += 1;
            (parentDeltaHash, bool changeStemToBranch) = TraverseStem((StemNode)child, traverseContext);
            traverseContext.CurrentIndex -= 1;
            if (changeStemToBranch)
            {
                BranchNode newChild = new BranchNode();
                newChild._internalCommitment.AddPoint(child._internalCommitment.Point);
                // since this is a new child, this would be just the parentDeltaHash.PointToField
                // now since there was a node before and that value is deleted - we need to subtract
                // that from the delta as well
                Fr deltaHash = newChild.UpdateCommitment(parentDeltaHash);
                _db.BranchTable[absolutePath] = newChild;
                deltaPoint = Committer.ScalarMul(deltaHash, pathIndex);
            }
            else
            {
                // in case of stem, no need to update the child commitment - because this commitment is the suffix commitment
                // pass on the update to upper level
                _db.BranchTable[absolutePath] = child;
                deltaPoint = parentDeltaHash;
            }
        }
        return deltaPoint;
    }

    private (Banderwagon, bool) TraverseStem(StemNode node, TraverseContext traverseContext)
    {
        (List<byte> sharedPath, byte? pathDiffIndexOld, byte? pathDiffIndexNew) =
            VerkleUtils.GetPathDifference(node.Stem, traverseContext.Stem.ToArray());

        if (sharedPath.Count != 31)
        {
            int relativePathLength = sharedPath.Count - traverseContext.CurrentIndex;
            // byte[] relativeSharedPath = sharedPath.ToArray()[traverseContext.CurrentIndex..].ToArray();
            byte oldLeafIndex = pathDiffIndexOld ?? throw new ArgumentException();
            byte newLeafIndex = pathDiffIndexNew ?? throw new ArgumentException();
            // node share a path but not the complete stem.

            // the internal node will be denoted by their sharedPath
            // 1. create SuffixNode for the traverseContext.Key - get the delta of the commitment
            // 2. set this suffix as child node of the BranchNode - get the commitment point
            // 3. set the existing suffix as the child - get the commitment point
            // 4. update the internal node with the two commitment points
            (Fr deltaHash, Commitment? suffixCommitment) = UpdateSuffixNode(traverseContext.Stem.ToArray(), traverseContext.LeafUpdateDelta, true);

            // creating the stem node for the new suffix node
            _db.BranchTable[sharedPath.ToArray().Concat(new[] { newLeafIndex }).ToArray()] =
                new StemNode(traverseContext.Stem.ToArray(), suffixCommitment!);
            Banderwagon newSuffixCommitmentDelta = Committer.ScalarMul(deltaHash, newLeafIndex);


            // instead on declaring new node here - use the node that is input in the function
            _db.StemTable.TryGetValue(node.Stem, out SuffixTree oldSuffixNode);
            _db.BranchTable[sharedPath.ToArray().Concat(new[] { oldLeafIndex }).ToArray()] =
                new StemNode(node.Stem, oldSuffixNode.ExtensionCommitment);

            Banderwagon oldSuffixCommitmentDelta =
                Committer.ScalarMul(oldSuffixNode.ExtensionCommitment.PointAsField, oldLeafIndex);

            Banderwagon deltaCommitment = oldSuffixCommitmentDelta + newSuffixCommitmentDelta;

            Banderwagon internalCommitment = FillSpaceWithInternalBranchNodes(sharedPath.ToArray(), relativePathLength, deltaCommitment);

            return (internalCommitment - oldSuffixNode.ExtensionCommitment.Point, true);
        }

        _db.StemTable.TryGetValue(traverseContext.Stem.ToArray(), out SuffixTree oldValue);
        Fr deltaFr = oldValue.UpdateCommitment(traverseContext.LeafUpdateDelta);
        _db.StemTable[traverseContext.Stem.ToArray()] = oldValue;

        return (Committer.ScalarMul(deltaFr, traverseContext.Stem[traverseContext.CurrentIndex - 1]), false);
    }

    private Banderwagon FillSpaceWithInternalBranchNodes(byte[] path, int length, Banderwagon deltaPoint)
    {
        for (int i = 0; i < length; i++)
        {
            BranchNode newInternalNode = new BranchNode();
            Fr upwardsDelta = newInternalNode.UpdateCommitment(deltaPoint);
            _db.BranchTable[path[..^i]] = newInternalNode;
            deltaPoint = Committer.ScalarMul(upwardsDelta, path[path.Length - i - 1]);
        }

        return deltaPoint;
    }

    private InternalNode? GetBranchChild(byte[] pathWithIndex)
    {
        return _db.BranchTable.TryGetValue(pathWithIndex, out InternalNode? child) ? child : null;
    }

    private (Fr, Commitment?) UpdateSuffixNode(byte[] stemKey, LeafUpdateDelta leafUpdateDelta, bool insertNew = false)
    {
        SuffixTree oldNode;
        if (insertNew) oldNode = new SuffixTree(stemKey);
        else _db.StemTable.TryGetValue(stemKey, out oldNode);

        Fr deltaFr = oldNode.UpdateCommitment(leafUpdateDelta);
        _db.StemTable[stemKey] = oldNode;
        // add the init commitment, because while calculating diff, we subtract the initCommitment in new nodes.
        return insertNew ? (deltaFr + oldNode.InitCommitmentHash, oldNode.ExtensionCommitment.Dup()) : (deltaFr, null);
    }

    private LeafUpdateDelta UpdateLeaf(Span<byte> key, byte[] value)
    {
        LeafUpdateDelta leafDelta = new LeafUpdateDelta();
        leafDelta.UpdateDelta(_updateLeaf(key.ToArray(), value), key[31]);
        return leafDelta;
    }
    private LeafUpdateDelta UpdateLeaf(Span<byte> stem, Dictionary<byte, byte[]> indexValuePairs)
    {
        byte[] key = new byte[32];
        stem.CopyTo(key);
        LeafUpdateDelta leafDelta = new LeafUpdateDelta();
        foreach ((byte index, byte[] value) in indexValuePairs)
        {
            key[31] = index;
            leafDelta.UpdateDelta(_updateLeaf(key, value), key[31]);
        }
        return leafDelta;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Banderwagon _updateLeaf(byte[] key, byte[] value)
    {
        _db.LeafTable.TryGetValue(key, out byte[]? oldValue);
        Banderwagon leafDeltaCommitment = GetLeafDelta(oldValue, value, key[31]);
        _db.LeafTable[key] = value;
        return leafDeltaCommitment;
    }

    private ref struct TraverseContext
    {
        public LeafUpdateDelta LeafUpdateDelta { get; }
        public Span<byte> Stem { get; }
        public int CurrentIndex { get; set; }

        public TraverseContext(Span<byte> stem, LeafUpdateDelta delta)
        {
            Stem = stem;
            CurrentIndex = 0;
            LeafUpdateDelta = delta;
        }
    }
}