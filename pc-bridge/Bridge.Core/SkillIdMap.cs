using System.Text.Json;
namespace PhantomDust.PcBridge.Core;
public sealed class SkillIdMap {
    private readonly Dictionary<string,string> ids;private readonly Dictionary<string,string> binary;
    public SkillIdMap(string json){ids=JsonSerializer.Deserialize<Dictionary<string,string>>(json)!;if(ids.Count!=375||Enumerable.Range(0,375).Any(n=>!ids.ContainsKey(n.ToString("D3")))||ids.Values.Any(x=>!System.Text.RegularExpressions.Regex.IsMatch(x,"^[0-9A-F]{2} [0-9A-F]{2}$"))||ids.Values.Distinct().Count()!=375||ids["000"]!="FF FF")throw new InvalidDataException("Incomplete or ambiguous retail skill mapping.");binary=ids.ToDictionary(x=>x.Value,x=>x.Key);}
    public string Decode(byte a,byte b)=>binary.GetValueOrDefault($"{a:X2} {b:X2}")??throw new InvalidDataException("Unknown/modded binary skill ID.");
    public byte[] Encode(string id)=>ids.TryGetValue(id,out var hex)?Convert.FromHexString(hex.Replace(" ","")):throw new InvalidDataException("Unknown skill ID.");
    public string[] DecodeArsenal(byte[] bytes){if(bytes.Length!=60)throw new InvalidDataException("Exactly 60 card bytes required.");return Enumerable.Range(0,30).Select(i=>Decode(bytes[i*2],bytes[i*2+1])).ToArray();}
}
