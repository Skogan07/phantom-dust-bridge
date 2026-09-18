using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class CollectionAccountingTests {
    static SnapshotDeck Arsenal(int slot,int copies)=>new(slot,"Test",1,Enumerable.Repeat("121",copies).Concat(Enumerable.Repeat("000",30-copies)).ToArray());
    [Fact] public void RemovingAssignedCopyIncreasesFreeWithoutChangingOwnership(){var totals=new Dictionary<string,int>{{"121",8}};var before=CollectionAccounting.FromTotals(totals,[Arsenal(1,1),Arsenal(12,1)],["121"]);var after=CollectionAccounting.FromTotals(totals,[Arsenal(1,0),Arsenal(12,1)],["121"]);Assert.Equal(6,before["121"].Free);Assert.Equal(7,after["121"].Free);Assert.Equal(8,after["121"].Total);Assert.Equal(12,Assert.Single(after["121"].Allocations).ArsenalSlot);Assert.Equal(8,totals["121"]);}
    [Fact] public void NegativeFreeIsUnknownNotClamped(){Assert.Throws<InvalidDataException>(()=>CollectionAccounting.FromTotals(new Dictionary<string,int>{{"121",0}},[Arsenal(1,1)],["121"]));}
    [Fact] public void IncompleteOwnershipIsRejected(){Assert.Throws<InvalidDataException>(()=>CollectionAccounting.FromTotals(new Dictionary<string,int>(),[Arsenal(1,0)],["121"]));}
    [Fact] public void AuraDoesNotConsumeOwnership(){Assert.Equal(8,CollectionAccounting.FromTotals(new Dictionary<string,int>{{"121",8}},[Arsenal(1,0)],["121"])["121"].Free);}
    [Fact] public void DuplicateCasesCannotDoubleCount(){Assert.Throws<InvalidDataException>(()=>CollectionAccounting.FromTotals(new Dictionary<string,int>{{"121",8}},[Arsenal(1,1),Arsenal(1,1)],["121"]));}
}
