using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace PrefabParsing {

    public static class Program {

        public static void Main(string[] args) {
            bool have = false;
            foreach (var p in args) {
                try {
                    TryApplyPrefabFix(p,File.ReadAllText(p));
                } catch (Exception e) {
                    Console.WriteLine($"found issue for {p}");
                    Console.WriteLine(e);
                    have = true;
                    // ignore
                }
            }
            Console.WriteLine($"Checked {args.Length} prefabs, errs: {have}");
        }

        static bool TryApplyPrefabFix(string path, string content) {
            // try get out var content
            if (TrySuggestPrefabFix(path,content,out var suggestedContent)) {
                Console.WriteLine($"!!! {path} is broken! Applying potential fix. You should check again with right click -> DoPrefabIntegrityCheck");
                File.WriteAllText(path,suggestedContent);
                return true;
            }
            Console.WriteLine($"no issues found for {path}");

            return false;

        }


        // NOTE arbitrarily chosen. Smallest possible prefab should for sure have like 20 lines (gameobject + rectTransform)
        const int shrunkPrefabLineCount = 15;

        class ParsedObj {
            public UnityYamlClassId type;
            public (int origLine, string id) id;
        }

        class ParsedGameObject : ParsedObj {
            public string name;
            public List<CompRef> compRefs;
            // dangling refs?
            public HashSet<string> danglingCompRefs;
            public int compRefsStart;
            public int origCompRefCount;
            public bool hasBrokenCompRefs;
            public string GetFirstDanglingId() {
                foreach (var item in danglingCompRefs) {
                    return item;
                }
                return "";
            }

            public void ApplyCompRefAddition(List<string> lines, string newRef) {
                ApplyNewCompRefs(lines,compRefs.Select(item => item.id).ToList().Concat(new [] {newRef}));
            }

            public void ApplyNewCompRefs(List<string> lines, IEnumerable<string> newRefs) {
                // Normally impossible, because every go has at least a ref to it's transform.
                if (compRefsStart == default) {
                    throw new PrefabParseException($"Go {name} on line {id.origLine} doesn't have comp ref syntax.");
                }
                lines.RemoveRange(compRefsStart,origCompRefCount);
                foreach (var elem in newRefs) {
                    lines.Insert(compRefsStart,$"  - component: {{fileID: {elem}}}");
                }
            }

        }

        // future maybe,
        // class ParsedTransform : ParsedComp {
            // children
            // father
        // }

        class ParsedComp : ParsedObj {
            public (int origLine, string goRef) goRef;
            /// Seems like always the go we are supported to be attached to
            #pragma warning disable CS0649
            public string enclosingGo;
            #pragma warning restore
        }

        class StrippedObj : ParsedObj {
            // prefab instance
            // file id + guid
        }

        class ParsedPrefabInstance : ParsedObj {
            // guid
            // father
            public List<(int origLine, string value)> transformRefs = new List<(int origLine, string value)>();
        }

        struct CompRef {
            public string id;
            public int origLine;
        }

        /// <summary>
        ///   throws exceptions when it finds something it cannot fix.
        ///   returns false if it doesn't find anything, true if there is an applicable fix
        /// </summary>
        public static bool TrySuggestPrefabFix(string path, string content, out string suggestedContent) {
            suggestedContent = "";
            var allComps = new List<ParsedComp>();
            var allPrefabInstances = new List<ParsedPrefabInstance>();
            var allFileIds = new HashSet<string>();
            var goLookup = new Dictionary<string,ParsedGameObject>();

            if (legacyCompRefRegex.IsMatch(content)) {
                Console.WriteLine($"{path} - Encountered legacy prefab syntax. Skipping. You can enable and disable a game object + save to force it up to date.");
                return false;
            }

            var lines = new List<string>();
            var splitLines = content.Split(new [] {"\n", "\r\n"}, StringSplitOptions.None);
            // remove last empty line
            for (var i = 0; i < splitLines.Length - 1; i++) {
                lines.Add(splitLines[i]);
            }

            if (lines.Count() < shrunkPrefabLineCount) {
                throw new NoFixAvaivableException($"{path} shrunk a lot.");
            }

            foreach (var item in ParsePrefab(lines)) {

                if (item is ParsedPrefabInstance prefabInstance) {

                    if (String.IsNullOrEmpty(prefabInstance.id.id)) {
                        throw new NoFixAvaivableException($"The id for a prefab instance at line {prefabInstance.id.origLine} is broken.");

                        // NOTE: fix involves checking all prefab refs for 'stripped' objects.
                        // not sure if the stripped objects are guaranteed to show up below the prefabInstance syntax (I guess so though)
                    }


                    allPrefabInstances.Add(prefabInstance);

                }

                if (item is StrippedObj strippedObj) {
                    if (String.IsNullOrEmpty(strippedObj.id.id))
                    throw new NoFixAvaivableException($"The id for a stripped Object at line {strippedObj.id.origLine} is broken.");

                    // NOTE: fix would involve checking all refs,
                    // e.g. script refs for monos
                    // lots of cases I guess

                }


                // NOTE if we where more advanced, we would handle transforms special

                allFileIds.Add(item.id.id);
                if (item is ParsedGameObject go) {

                    if (String.IsNullOrEmpty(go.id.id)) {
                        // NOTE fix might not be that hard; Check all go refs in the file and there might be a dangling one
                        throw new NoFixAvaivableException($"Broken go id on line {go.id.origLine}. Fix not supported yet.");
                    }

                    goLookup.Add(go.id.id,go);
                }

                if (item is ParsedComp comp) {
                    allComps.Add(comp);
                }

            }

            foreach (var go in goLookup.Values) {
                if (go.compRefs == null) {
                    throw new PrefabParseException($"Comp refs null for go on line {go.id.origLine}, legacy prefab? Try disable + enable a gameobject, then save the prefab. Unity will make syntax up to date.");
                }
                foreach (var compRef in go.compRefs) {
                    if (String.IsNullOrEmpty(compRef.id)) {
                        go.hasBrokenCompRefs = true;
                    } else if (!allFileIds.Contains(compRef.id)) {
                        if (go.danglingCompRefs == null) {
                            go.danglingCompRefs = new HashSet<string>();
                        }
                        go.danglingCompRefs.Add(compRef.id);
                    }
                }

            }

            // -- lookups filled --


            // these are the known comps that are subscribing to Key gameobject id
            var goIdRefs = new Dictionary<string,List<string>>();

            foreach (var prefabInstance in allPrefabInstances) {
                foreach (var transformRef in prefabInstance.transformRefs) {
                    if (transformRef.value != "0" &&
                        (String.IsNullOrEmpty(transformRef.value) || !allFileIds.Contains(transformRef.value))) {
                        throw new NoFixAvaivableException($"Broken prefab transform ref at line {transformRef.origLine}, you can try to figure out to which transform it should belong and put the file id.");
                    }
                }
            }

            foreach (var comp in allComps) {

                if (String.IsNullOrEmpty(comp.goRef.goRef) || !allFileIds.Contains(comp.goRef.goRef)) {
                    // NOTE: Fix would almost be easy, I would add the id of the enclosing comp.
                    // Only issue is the edge case with override comps.
                    // Question is if you are looking at an override comp and to which stripped obj are you would be supposed to add this now.
                    throw new NoFixAvaivableException($"Component on line {comp.id.origLine} has a broken go ref.");
                }


                if (!goIdRefs.TryGetValue(comp.goRef.goRef, out var list)) {
                    goIdRefs.Add(comp.goRef.goRef, new List<string>());
                }
                goIdRefs[comp.goRef.goRef].Add(comp.id.id);


                if (String.IsNullOrEmpty(comp.id.id)) {
                    if (comp.type == UnityYamlClassId.Transform || comp.type == UnityYamlClassId.RectTransform) {
                        throw new NoFixAvaivableException("Encountered broken file id on a transform. Fix would be complex.");
                    }

                    if (goLookup.TryGetValue(comp.goRef.goRef, out ParsedGameObject foundGo)) {

                        // null implies count == 0.
                        if (foundGo.danglingCompRefs == null) {
                            var newId = newUniqueFileId();
                            InsertFileId(lines,comp.id.origLine,newId);
                            foundGo.ApplyCompRefAddition(lines,newId);
                            return ApplyFix(out suggestedContent);

                        } else if (foundGo.danglingCompRefs.Count == 1) {

                            InsertFileId(lines,comp.id.origLine,foundGo.GetFirstDanglingId());
                            // take this as the id for the comp
                            return ApplyFix(out suggestedContent);

                        } else {
                            throw new NoFixAvaivableException($"Found go on line {foundGo.id.origLine} for broken id on line {comp.id.origLine} but it has multiple dangling comp refs.");
                        }

                    } else {

                        throw new NoFixAvaivableException($"Broken id on line {comp.id.origLine} and don't know any corresponding game object.");

                    }

                }

            }

            // broken comp refs

            foreach (var go in goLookup.Values) {
                if (goIdRefs.TryGetValue(go.id.id, out var knownGoRefs)) {
                    if (knownGoRefs.Count != go.compRefs.Count || go.hasBrokenCompRefs) {
                        go.ApplyNewCompRefs(lines, knownGoRefs);
                        return ApplyFix(out suggestedContent);
                    }


                } else {
                    throw new NoFixAvaivableException($"No single comp is referencing {go.name}, (line: {go.id.origLine})");
                }

            }

            return false;


            // NOTE we only allow 1 fix at a time.
            // It's very unlikely that there are multiple broken things at the same time
            // this way we don't have to keep track of moving lines (I would do it with an <int,int> dict if I wanted to go there.)

            // asume lines changed.
            bool ApplyFix(out string _newContent) {
                var sb = new StringBuilder();
                foreach (var line in lines) {
                    sb.AppendLine(line);
                }
                _newContent = sb.ToString();
                return true;
            }


            string newUniqueFileId() {
                var fileIdNum = -1337;
                var safetyCounter = 0;
                const int maxTries = 100_000;
                while (allFileIds.Contains(fileIdNum.ToString()) && safetyCounter++ < maxTries) {
                    fileIdNum++;
                }
                if (safetyCounter >= maxTries) {
                    throw new PrefabParseException("Failed to get unique id");
                }
                return fileIdNum.ToString();
            }

        }

        static void InsertFileId(List<string> lines, int index, string fileId) {
            var line = lines[index];
            if (line.IndexOf("&") < 0) {
                throw new PrefabParseException($"Expected an & in line {index}");
            }

            lines[index] = line.Insert(line.IndexOf("&") + 1,fileId);
        }

        static List<ParsedObj> ParsePrefab(List<string> lines) {
            var objs = new List<ParsedObj>();
            var currLine = 0;
            ParsedObj currObj = null;

            foreach (var line in lines) {

                if (objBeginningRegex.Match(line).Success) {
                    if (currObj != null) {
                        objs.Add(currObj);
                    }
                    if (TryParseCompBeginningLine(line, out var type, out var id)) {
                        if (strippedObjBeginningRegex.Match(line).Success) {
                            currObj = new StrippedObj();
                        } else if (type == UnityYamlClassId.GameObject) {
                            currObj = new ParsedGameObject();
                        } else if (type == UnityYamlClassId.PrefabInstance) {
                            currObj = new ParsedPrefabInstance();
                        } else {
                            currObj = new ParsedComp();
                        }
                        currObj.id = (currLine,id);
                        currObj.type = type;
                    }

                } else if (currObj is ParsedGameObject gameObject) {
                    if (compRefRegex.Match(line).Success) {
                        if (gameObject.compRefs == null) {
                            gameObject.compRefsStart = currLine;
                            gameObject.compRefs = new List<CompRef>();
                        }
                        gameObject.compRefs.Add(new CompRef() { id = compRefRegex.Match(line).Groups[1].ToString(), origLine = currLine });
                        gameObject.origCompRefCount++;
                    } else if (goNameRegex.Match(line).Success) {
                        gameObject.name = goNameRegex.Match(line).Groups[1].ToString();
                    }
                } else if (currObj is ParsedComp comp && gameObjRefRegex.Match(line).Success) {
                    comp.goRef = (currLine,gameObjRefRegex.Match(line).Groups[1].ToString());
                } else if (currObj is ParsedPrefabInstance prefabInstance && prefabTransformRegex.Match(line).Success) {
                    prefabInstance.transformRefs.Add((currLine,prefabTransformRegex.Match(line).Groups[1].ToString()));
                }

                currLine++;
            }

            if (currObj != null) {
                objs.Add(currObj);
            }

            return objs;
        }

        static bool TryParseCompBeginningLine(string line, out UnityYamlClassId typeNum, out string id) {
            typeNum = default;
            id = default;
            if (!objBeginningRegex.IsMatch(line)) return false;
            var match = objBeginningRegex.Match(line);
            id = match.Groups[2].ToString();
            typeNum = default;
            if (Int32.TryParse(match.Groups[1].ToString(), out var result)) {
                typeNum = (UnityYamlClassId)result;
                return true;
            }
            return false;
        }

        static Regex compRefRegex = new Regex(@"  - component: {fileID: (-?\d+)?}");
        static Regex gameObjRefRegex = new Regex(@"  m_GameObject: {fileID: (-?\d+)?}");
        static Regex objBeginningRegex = new Regex(@"--- !u!(\d+) &(-?\d+)?");
        static Regex strippedObjBeginningRegex = new Regex(@"--- !u!(\d+) &(-?\d+)? stripped");

        //static Regex transformChildRegex = new Regex(@"  - {fileID: (-?\d+)?}");
        static Regex prefabTransformRegex = new Regex(@"    m_TransformParent: {fileID: (-?\d+)?}");
        static Regex goNameRegex = new Regex(@"  m_Name: (\w+)");
        static Regex legacyCompRefRegex = new Regex(@"  - (-?\d+): {fileID: (-?\d+)?}");

        }

        public class NoFixAvaivableException : Exception {
            public NoFixAvaivableException(string message) : base(message) { }
        }

        public class PrefabParseException : Exception {
            public PrefabParseException(string message) : base(message) { }
        }

}
