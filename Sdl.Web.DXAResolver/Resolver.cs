using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Tridion.ContentManager;
using Tridion.ContentManager.CommunicationManagement;
using Tridion.ContentManager.CommunicationManagement.Regions;
using Tridion.ContentManager.ContentManagement;
using Tridion.ContentManager.ContentManagement.Fields;
using Tridion.ContentManager.Publishing;
using Tridion.ContentManager.Publishing.Resolving;

namespace Sdl.Web.DXAResolver
{   
    /// <summary>
    /// DXA Custom Resolver to publish components added to a page with the 'Generate Data Presentation' template.
    /// 
    /// Usage:
    ///   1) Add this assembly to the GAC on the machine hosting the CME
    ///   2) Modify the \config\Tridion.ContentManager.config file and add the following to the resolving/mappings section:
    ///         <resolving>
    ///             <mappings>
    ///                 ...
    ///                 <add itemType="Tridion.ContentManager.CommunicationManagement.Page">
    ///                     <resolvers>
    ///                         ...
    ///                         <add type="Sdl.Web.DXAResolver.Resolver" assembly="Sdl.Web.DXAResolver, Version=1.0.0.0, Culture=neutral, PublicKeyToken=b9a30ebcbde732e4" />
    ///                     </resolvers>
    ///                 </add>
    ///             </mappings>
    ///         </resolving>
    /// </summary>
    public class Resolver : IResolver
    {
        private int _maxRecurseDepth = -1; // default = infinite recursion depth
        private readonly LogAdapter _log;
        public Resolver()
        {
            // init logging
            _log = new LogAdapter(GetType());
            _log.Debug($"DXA Custom Resolver initialised with recurse depth {_maxRecurseDepth}");            
        }       

        private List<Component> GatherLinkedComponents(Component component)
        {
            _log.Debug($"Gathering linked components for component {component.Title}");
            List<ItemFields> fieldList = new List<ItemFields>();
            List<Component> components = new List<Component>();
            if (component.Content != null)
            {
                fieldList.Add(new ItemFields(component.Content, component.Schema));
            }
            if (component.Metadata != null)
            {
                fieldList.Add(new ItemFields(component.Metadata, component.MetadataSchema));
            }            
            var componentLinkFields = new List<ComponentLinkField>();
            var embeddedSchemaFields = new List<EmbeddedSchemaField>();
            foreach (var fields in fieldList)
            {
                foreach (var field in fields)
                {
                    if (field is ComponentLinkField)
                    {
                        componentLinkFields.Add((ComponentLinkField)field);
                    }

                    if (field is EmbeddedSchemaField)
                    {
                        embeddedSchemaFields.Add((EmbeddedSchemaField) field);
                    }
                }
            }

            for (int i = 0; i < embeddedSchemaFields.Count; i++)
            {
                EmbeddedSchemaField linkField = embeddedSchemaFields[i];
                EmbeddedSchemaFieldDefinition linksFieldDefinition = linkField.Definition as EmbeddedSchemaFieldDefinition;
                if (linksFieldDefinition?.EmbeddedSchema == null) continue;
                TcmUri id = linksFieldDefinition.EmbeddedSchema.Id; // force schema load
                foreach (var embeddedFields in linkField.Values)
                {
                    foreach (var embeddedField in embeddedFields)
                    {
                        if (embeddedField is ComponentLinkField)
                        {
                            componentLinkFields.Add((ComponentLinkField)embeddedField);
                        }
                        if (embeddedField is EmbeddedSchemaField)
                        {
                            embeddedSchemaFields.Add((EmbeddedSchemaField)embeddedField);
                        }
                    }
                }
            }

            HashSet<TcmUri> linkedComponents = new HashSet<TcmUri>();
            foreach (var linkField in componentLinkFields)
            {
                if (linkField.Values != null)
                {
                    foreach (var c in linkField.Values)
                    {
                        if(linkedComponents.Contains(c.Id)) continue;
                        linkedComponents.Add(c.Id);
                        components.Add(c);
                    }
                }
            }
            return components;
        }
      
        private bool ContinueRecursion(int depth) => _maxRecurseDepth < 0 || depth <= _maxRecurseDepth;

        private ResolvedItem ResolveComponent(Component component, ComponentTemplate template, HashSet<IdentifiableObject> resolved, int recurseLevel)
        {
            _log.Debug($"Attempting to resolve component: title='{component.Title}' id='{component.Id}' schema='{component.Schema.Title}'");
            if (!ContinueRecursion(recurseLevel))
            {
                _log.Debug($"Reached max recusion level when trying to resolve component {component.Id}, skipping.");
                return null;
            }
            if (resolved.Contains(component))
            {
                _log.Debug($"Already resolved component {component.Id}, skipping.");
                return null;
            }            
            TcmUri schemaId = component.Schema.Id; // force load of schema!
            if (template.RelatedSchemas.Contains(component.Schema))
            {
                return new ResolvedItem(component, template);
            }
            return null;
        }

        private List<ComponentPresentation> GatherComponentPresentations(IList<IRegion> regions)
        {
            List<ComponentPresentation> cps = new List<ComponentPresentation>();
            if (regions == null) return cps;
            foreach (var region in regions)
            {
                if (region.ComponentPresentations != null)
                    cps.AddRange(region.ComponentPresentations);

                cps.AddRange(GatherComponentPresentations(region.Regions));
            }
            return cps;
        }

        private List<ComponentPresentation> GatherComponentPresentations(Page page)
        {
            _log.Debug("Gathering component presentations for page...");
            List<ComponentPresentation> cps = new List<ComponentPresentation>();
            // Get root level component presentations that do not appear under any native regions
            cps.AddRange(page.ComponentPresentations);
            // Get all component presentations that appear under native regions
            cps.AddRange(GatherComponentPresentations(page.Regions));            
            _log.Debug($"Found {cps.Count} component presentations");
            return cps;
        }

        private List<ResolvedItem> ResolveDataPresentations(IdentifiableObject item,
            ComponentTemplate template, HashSet<IdentifiableObject> resolved, int recurseLevel)
        {
            _log.Debug($"Analyzing item for resolving of type: '{item.GetType().Name}' id: '{item.Id}' title: '{item.Title}'");
            List<ResolvedItem> toResolve = new List<ResolvedItem>();
            if (!ContinueRecursion(recurseLevel))
            {
                _log.Debug($"Reached max recusion level when trying to resolve item {item.Id}, skipping.");
                return toResolve;
            }
            List<Component> components = new List<Component>();           
            if (item is Page)
            {
                var page = (Page) item;
                _log.Debug($"Resolving page '{page.Title}' Id={page.Id}");
                if (resolved.Contains(page))
                {
                    _log.Debug("  * already resolved this page, skipping !");
                    return toResolve;
                }
                components.AddRange(GatherComponentPresentations(page).Select(cp => cp.Component));
            }
            if (item is Component)
            {
                _log.Debug($"Resolving component '{item.Title}' Id={item.Id}");
                components.Add(item as Component);
            }
            if (components.Count <= 0) return toResolve;
            var toProcess = new HashSet<Component>();
            var depths = new Dictionary<string, int>();
            for (int i = 0; i < components.Count; i++)
            { // note: avoiding recursive approach here so we can truely do infinite depth without stack overflows
                var c = components[i];
                int depth = recurseLevel;
                if (depths.ContainsKey(c.Id)) depth = depths[c.Id];
                if (!ContinueRecursion(depth)) continue;
                if (toProcess.Contains(c)) continue;
                toProcess.Add(c);
                List<Component> linked = GatherLinkedComponents(c);
                foreach (var linkedComponent in linked)
                {
                    if (!ContinueRecursion(depth + 1)) break;
                    if (toProcess.Contains(linkedComponent)) continue;
                    components.Add(linkedComponent);
                    if (depths.ContainsKey(linkedComponent.Id))
                    {
                        depths[linkedComponent.Id] = depth + 1;
                    }
                    else
                    {
                        depths.Add(linkedComponent.Id, depth + 1);
                    }                    
                }
            }
            toResolve.AddRange(toProcess.Select(
                component => ResolveComponent(component, template, resolved, recurseLevel)).Where(r => r != null));
            return toResolve;
        }

        /// <summary>
        /// Entry point for resolver.
        /// </summary>
        /// <param name="item">Item to resolve</param>
        /// <param name="instruction">Resolve instruction</param>
        /// <param name="context">Publish context</param>
        /// <param name="resolvedItems">Final items to resolve</param>
        public void Resolve(IdentifiableObject item, ResolveInstruction instruction, PublishContext context,
            Tridion.Collections.ISet<ResolvedItem> resolvedItems)
        {
            if (instruction.Purpose != ResolvePurpose.Publish && instruction.Purpose != ResolvePurpose.RePublish ||
                _maxRecurseDepth == 0)
                return;         
            _log.Debug("DXA Custom Resolver started...");
            _log.Debug("Loading global app data:");
            const string appId = "dxa:CustomResolver";
            try
            {
                var appData = context.Session.SystemManager.LoadGlobalApplicationData(appId);
                if (appData != null)
                {
                    _log.Debug("Found configuration for custom resolver in global app data. Attempting to parse.");
                    string xml = string.Empty;
                    if (appData.TypeId?.StartsWith("XmlElement:") ?? false)
                    {
                        _log.Debug($"Found configuration using XElement appData type.");
                        string @string = Encoding.UTF8.GetString(appData.Data);
                        XmlDocument xmlDocument = new XmlDocument();
                        xmlDocument.LoadXml(@string);
                        xml = xmlDocument.DocumentElement?.OuterXml;
                    }
                    if (string.IsNullOrEmpty(xml))
                    {
                        _log.Debug("Assuming string content for xml.");
                        xml = Encoding.Unicode.GetString(appData.Data);
                    }
                    _log.Debug($"xml={xml}");
                    XElement parsedXml = XElement.Parse(xml);
                    foreach (XElement xe in parsedXml.Elements())
                    {
                        if (!xe.Name.LocalName.Equals("RecurseDepth")) continue;
                        _log.Debug($"Found RecurseDepth value of '{xe.Value}'.");
                        _maxRecurseDepth = int.Parse(xe.Value);
                        break; // only one setting at moment
                    }
                }
                else
                {
                    _log.Debug(
                        $"Custom resolver configuration not found in global app data for '{appId}'. Using default settings.");
                    _maxRecurseDepth = -1;
                }
            }
            catch (Exception e)
            {
                _log.Debug(
                    $"Exception occured when reading global app data for application id '{appId}'. Using default settings.");
                _log.Debug(e.Message);
                _maxRecurseDepth = -1;
            }
            
            _log.Debug($"Using _maxRecurseDepth={_maxRecurseDepth}");

            try
            {
                var sourceItem = (RepositoryLocalObject) item;

                _log.Debug($"Analyzing source item of type: {item.GetType().Name} with id: {item.Id} and title: {item.Title}");

                var contextPublication = (Publication) sourceItem.ContextRepository;
                var filter = new ComponentTemplatesFilter(item.Session)
                {
                    AllowedOnPage = false,
                    BaseColumns = ListBaseColumns.IdAndTitle
                };
                const string dataPresentationTemplateTitle = "Generate Data Presentation";
                var dataPresentationTemplate = contextPublication.GetComponentTemplates(filter).FirstOrDefault(
                    ct => ct.Title == dataPresentationTemplateTitle);
                if (dataPresentationTemplate == null)
                {
                    _log.Debug("No 'Generate Data Presentation' component template found. Skipping custom resolver.");
                    return;
                }
                var resolved = new HashSet<IdentifiableObject>();
                Stopwatch t = new Stopwatch();
                t.Start();

                HashSet<TcmUri> alreadyResolved = new HashSet<TcmUri>();
                foreach (var x in resolvedItems)
                {
                    alreadyResolved.Add(x.Item.Id);
                }

                List<ResolvedItem> resolvedItemsCopy = new List<ResolvedItem>(resolvedItems);
                foreach (var resolvedItem in resolvedItemsCopy)
                {
                    foreach (var dataPresentation in ResolveDataPresentations(resolvedItem.Item, dataPresentationTemplate, resolved, 0))
                    {
                        if (alreadyResolved.Contains(dataPresentation.Item.Id))
                        {
                            _log.Debug($"  > Already resolved item '{dataPresentation.Item.Title}' with id: {dataPresentation.Item.Id}");
                            continue;
                        }
                        _log.Debug($"  > Resolved item '{dataPresentation.Item.Title}' with id: {dataPresentation.Item.Id}");
                        alreadyResolved.Add(dataPresentation.Item.Id);
                        resolvedItems.Add(dataPresentation);
                    }
                }
               
                t.Stop();              
                _log.Debug($"DXA Custom Resolver took {t.ElapsedMilliseconds}ms to complete.");
            }
            catch (Exception e)
            {
                _log.Debug($"Exception occured: {e.Message}");
                _log.Debug($"Stacktrace: {e.StackTrace}");
                throw;
            }
        }
    }
}
