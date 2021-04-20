
using Opc.Ua.Export;
using Opc.Ua.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Net.Mime;
using System.Text;
using System.Threading;

namespace Opc.Ua.Sample
{
    public enum StationStatus : int
    {
        Ready = 0,
        WorkInProgress = 1,
        Fault = 2
    }

    public class StationNodeManager : CustomNodeManager2
    {
        private Timer m_stationClock;

        private ulong m_overallRunningTime = 0;
        private ulong m_faultyTime = 0;
        private ulong m_idealCycleTime = 5000;
        private ulong m_actualCycleTime = 5000;
        private StationStatus m_status = StationStatus.Ready;
        private ulong m_energyConsumption = 1000;
        private ulong m_pressure = 1000;
        private ulong m_productSerialNumber = 1;
        private ulong m_numberOfManufacturedProducts = 1;
        private ulong m_numberOfDiscardedProducts = 0;

        private ushort m_namespaceIndex;
        private long m_lastUsedId;

        private NodeId m_NumberOfManufacturedProductsID;
        private NodeId m_NumberOfDiscardedProductsID;
        private NodeId m_ProductSerialNumberID;
        private NodeId m_ActualCycleTimeID;
        private NodeId m_EnergyConsumptionID;
        private NodeId m_FaultyTimeID;
        private NodeId m_IdealCycleTimeID;
        private NodeId m_OverallRunningTimeID;
        private NodeId m_PressureID;
        private NodeId m_StatusID;
        private ApplicationConfiguration m_Configuration;

        public StationNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration)
        {
            SystemContext.NodeIdFactory = this;
            m_Configuration = configuration;

            List<string> namespaceUris = new List<string>();
            namespaceUris.Add("http://opcfoundation.org/UA/Station/");
            NamespaceUris = namespaceUris;

            m_namespaceIndex = Server.NamespaceUris.GetIndexOrAppend(namespaceUris[0]);
            m_lastUsedId = 0;
            m_stationClock = new Timer(Tick, this, Timeout.Infinite, (int)m_actualCycleTime);
        }

        public override NodeId New(ISystemContext context, NodeState node)
        {
            uint id = Utils.IncrementIdentifier(ref m_lastUsedId);
            return new NodeId(id, m_namespaceIndex);
        }

        private FolderState CreateFolder(NodeState parent, string path, string name)
        {
            FolderState folder = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(path, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(folder);

            return folder;
        }

        private MethodState CreateMethod(NodeState parent, string path, string name)
        {
            MethodState method = new MethodState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(path, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };

            parent?.AddChild(method);

            return method;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                IList<IReference> references = null;
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }
                
                ImportNodeset2Xml(externalReferences, m_Configuration.ApplicationUri);  //Use passed NodeSet file, instead of hard coded

                FolderState root = CreateFolder(null, "AssetAdminShell", "AssetAdminShell");
                root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
                root.EventNotifier = EventNotifiers.SubscribeToEvents;
                AddRootNotifier(root);

                MethodState createAASMethod = CreateMethod(root, "GenerateAAS", "GenerateAAS");
                createAASMethod.OnCallMethod = new GenericMethodCalledEventHandler(OnGenerateAASCall);

                AddPredefinedNode(SystemContext, root);

                AddReverseReferences(externalReferences);
            }
        }

        private ServiceResult OnGenerateAASCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            try
            {
                string packagePath = Path.Combine(Directory.GetCurrentDirectory(), "Station.aasx");
                using (Package package = Package.Open(packagePath, FileMode.Create))
                {
                    // add package origin part
                    PackagePart origin = package.CreatePart(new Uri("/aasx/aasx-origin", UriKind.Relative), MediaTypeNames.Text.Plain, CompressionOption.Maximum);

                    using (Stream fileStream = origin.GetStream(FileMode.Create))
                    {
                        var bytes = Encoding.ASCII.GetBytes("Intentionally empty.");
                        fileStream.Write(bytes, 0, bytes.Length);
                    }

                    package.CreateRelationship(origin.Uri, TargetMode.Internal, "http://www.admin-shell.io/aasx/relationships/aasx-origin");

                    // add package spec part
                    PackagePart spec = package.CreatePart(new Uri("/aasx/aasenv-with-no-id/aasenv-with-no-id.aas.xml", UriKind.Relative), MediaTypeNames.Text.Xml);

                    string packageSpecPath = Path.Combine(Directory.GetCurrentDirectory(), "aasenv-with-no-id.aas.xml");
                    using (FileStream fileStream = new FileStream(packageSpecPath, FileMode.Open, FileAccess.Read))
                    {
                        CopyStream(fileStream, spec.GetStream());
                    }

                    origin.CreateRelationship(spec.Uri, TargetMode.Internal, "http://www.admin-shell.io/aasx/relationships/aas-spec");

                    // add the nodeset2.xml as a supplemental document part
                    PackagePart supplementalDoc = package.CreatePart(new Uri("/aasx/Station.NodeSet2.xml", UriKind.Relative), MediaTypeNames.Text.Xml);

                    string documentPath = Path.Combine(Directory.GetCurrentDirectory(), "Station.NodeSet2.xml");
                    using (FileStream fileStream = new FileStream(documentPath, FileMode.Open, FileAccess.Read))
                    {
                        CopyStream(fileStream, supplementalDoc.GetStream());
                    }

                    package.CreateRelationship(supplementalDoc.Uri, TargetMode.Internal, "http://www.admin-shell.io/aasx/relationships/aas-suppl");
                }

                return ServiceResult.Good;
            }
            catch (Exception ex)
            {
                return ServiceResult.Create(ex, StatusCodes.BadUnexpectedError, "Failed to create Asset Admin Shell!");
            }
        }

        private void CopyStream(Stream source, Stream target)
        {
            const int bufSize = 0x1000;
            byte[] buf = new byte[bufSize];
            int bytesRead = 0;
            while ((bytesRead = source.Read(buf, 0, bufSize)) > 0)
            {
                target.Write(buf, 0, bytesRead);
            }
        }

        private void ImportNodeset2Xml(IDictionary<NodeId, IList<IReference>> externalReferences, string resourcepath)
        {
            NodeStateCollection predefinedNodes = new NodeStateCollection();

            Stream stream = new FileStream(resourcepath, FileMode.Open);
            UANodeSet nodeSet = UANodeSet.Read(stream);

            foreach (string namespaceUri in nodeSet.NamespaceUris)
            {
                SystemContext.NamespaceUris.GetIndexOrAppend(namespaceUri);
            }
            nodeSet.Import(SystemContext, predefinedNodes);

            for (int i = 0; i < predefinedNodes.Count; i++)
            {
                try { 
                    AddPredefinedNode(SystemContext, predefinedNodes[i]);
                } catch(Exception ex)
                {
                    Console.WriteLine("Error adding node to nodeset: " + ex.Message);
                    Console.WriteLine("Trying to continue...");
                }
            }
        }

        protected override NodeState AddBehaviourToPredefinedNode(ISystemContext context, NodeState predefinedNode)
        {
            // add behaviour to our methods
            MethodState methodState = predefinedNode as MethodState;
            if (methodState != null)
            {
                if (methodState.DisplayName == "Execute")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(Execute);

                    // define the method's input argument (the serial number)
                    methodState.InputArguments = new PropertyState<Argument[]>(methodState)
                    {
                        NodeId = new NodeId(methodState.BrowseName.Name + "InArgs", NamespaceIndex),
                        BrowseName = BrowseNames.InputArguments
                    };
                    methodState.InputArguments.DisplayName = methodState.InputArguments.BrowseName.Name;
                    methodState.InputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
                    methodState.InputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
                    methodState.InputArguments.DataType = DataTypeIds.Argument;
                    methodState.InputArguments.ValueRank = ValueRanks.OneDimension;

                    methodState.InputArguments.Value = new Argument[]
                    {
                        new Argument { Name = "SerialNumber", Description = "Serial number of the product to make.",  DataType = DataTypeIds.UInt64, ValueRank = ValueRanks.Scalar }
                    };

                    return predefinedNode;
                }
                if (methodState.DisplayName == "Reset")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(Reset);
                    return predefinedNode;
                }
                if (methodState.DisplayName == "OpenPressureReleaseValve")
                {
                    methodState.OnCallMethod = new GenericMethodCalledEventHandler(OpenPressureReleaseValve);
                    return predefinedNode;
                }
            }

            // also capture the nodeIDs of our instance variables (i.e. NOT the model!)
            BaseDataVariableState variableState = predefinedNode as BaseDataVariableState;
            if ((variableState != null) && (variableState.ModellingRuleId == null))
            {
                if (variableState.DisplayName == "NumberOfManufacturedProducts")
                {
                    m_NumberOfManufacturedProductsID = variableState.NodeId;
                }
                if (variableState.DisplayName == "NumberOfDiscardedProducts")
                {
                    m_NumberOfDiscardedProductsID = variableState.NodeId;
                }
                if (variableState.DisplayName == "ProductSerialNumber")
                {
                    m_ProductSerialNumberID = variableState.NodeId;
                }
                if (variableState.DisplayName == "ActualCycleTime")
                {
                    m_ActualCycleTimeID = variableState.NodeId;
                }
                if (variableState.DisplayName == "EnergyConsumption")
                {
                    m_EnergyConsumptionID = variableState.NodeId;
                }
                if (variableState.DisplayName == "FaultyTime")
                {
                    m_FaultyTimeID = variableState.NodeId;
                }
                if (variableState.DisplayName == "IdealCycleTime")
                {
                    m_IdealCycleTimeID = variableState.NodeId;
                }
                if (variableState.DisplayName == "OverallRunningTime")
                {
                    m_OverallRunningTimeID = variableState.NodeId;
                }
                if (variableState.DisplayName == "Pressure")
                {
                    m_PressureID = variableState.NodeId;
                }
                if (variableState.DisplayName == "Status")
                {
                    m_StatusID = variableState.NodeId;
                }
            }

            return predefinedNode;
        }

        private ServiceResult Execute(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            if (m_status == StationStatus.Fault)
            {
                ServiceResult result = new ServiceResult(new Exception("Machine is in fault state, call reset first!"));
                return result;
            }

            m_productSerialNumber = (ulong)inputArguments[0];

            m_status = StationStatus.WorkInProgress;

            m_stationClock.Change((int)m_actualCycleTime, (int)m_actualCycleTime);

            return ServiceResult.Good;
        }

        private ServiceResult Reset(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            m_status = StationStatus.Ready;

            return ServiceResult.Good;
        }

        private ServiceResult OpenPressureReleaseValve(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            m_pressure = 1000;

            return ServiceResult.Good;
        }

        private void UpdateVariables()
        {
            NodeState node = Find(m_NumberOfManufacturedProductsID);
            BaseDataVariableState variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_numberOfManufacturedProducts;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_NumberOfDiscardedProductsID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_numberOfDiscardedProducts;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_ProductSerialNumberID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_productSerialNumber;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_ActualCycleTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_actualCycleTime;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_EnergyConsumptionID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_energyConsumption;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_FaultyTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_faultyTime;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_IdealCycleTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_idealCycleTime;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_OverallRunningTimeID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_overallRunningTime;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_PressureID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_pressure;
                variableState.ClearChangeMasks(SystemContext, false);
            }

            node = Find(m_StatusID);
            variableState = node as BaseDataVariableState;
            if (variableState != null)
            {
                variableState.Value = m_status;
                variableState.ClearChangeMasks(SystemContext, false);
            }
        }

        private static void Tick(object state)
        {
            StationNodeManager nodeManager = (StationNodeManager) state;

            if (nodeManager.m_status == StationStatus.Fault)
            {
                return;
            }

            if (nodeManager.m_status == StationStatus.WorkInProgress)
            {
                // we produce a discarded product every 100 parts
                // we go into fault mode every 1000 parts
                if ((nodeManager.m_numberOfManufacturedProducts % 1000) == 0)
                {
                    nodeManager.m_status = StationStatus.Fault;
                }
                else if ((nodeManager.m_numberOfManufacturedProducts % 100) == 0)
                {
                    nodeManager.m_numberOfDiscardedProducts++;
                    nodeManager.m_productSerialNumber++;
                }
                else
                {
                    nodeManager.m_numberOfManufacturedProducts++;
                    nodeManager.m_productSerialNumber++;
                }
            }

            nodeManager.UpdateVariables();
        }
    }
}
