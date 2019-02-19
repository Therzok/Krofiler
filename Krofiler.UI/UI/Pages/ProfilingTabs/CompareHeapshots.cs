using System;
using System.Collections.Generic;
using Eto.Forms;
using System.Collections;
using System.Linq;
namespace Krofiler
{
	public class CompareHeapshotsTab : Panel, IProfilingTab
	{
		readonly KrofilerSession session;
		readonly Heapshot newHeapshot;
		readonly Heapshot oldHeapshot;

		FilterCollection<TypeChangeInfo> typesCollection = new FilterCollection<TypeChangeInfo>();

		public string Title {
			get {
				return "Compare";
			}
		}

		public string Details {
			get {
				return oldHeapshot.Name + " " + newHeapshot.Name;
			}
		}

		public Control TabContent {
			get {
				return this;
			}
		}

		public event InsertTabDelegate InsertTab;

		class TypeChangeInfo
		{
			public string TypeName;
			public long TypeId;
			public LazyObjectsList NewObjects;
			public LazyObjectsList NewFinalizableObjects;
			public LazyObjectsList NonFinalizableObjects;
			public LazyObjectsList DeadObjects;
			public LazyObjectsList FinalizableObjects;
			public LazyObjectsList NewHsObjects;
			public LazyObjectsList OldHsObjects;
		}

		TextBox filterTypesTextBox;

		static EmptyObjectsList EmptyList = EmptyObjectsList.Instance;

		public CompareHeapshotsTab(KrofilerSession session, Heapshot hs1, Heapshot hs2)
		{
			this.session = session;
			if (hs2.Id > hs1.Id) {
				newHeapshot = hs2;
				oldHeapshot = hs1;
			} else {
				newHeapshot = hs1;
				oldHeapshot = hs2;
			}
			var diff = new DiffHeap(oldHeapshot, newHeapshot);
			var newObjects = diff.NewObjects;
			var newFinalizableObjects = diff.NewFinalizableObjects;
			var deleted = diff.DeletedObjects;
			var allObjectsInOldHs = oldHeapshot.TypesToObjectsListMap;
			var allObjectsInNewHs = newHeapshot.TypesToObjectsListMap;
			var allFinalizableObjectsInNewHs = newHeapshot.TypesToFinalizableObjectsListMap;
			var allNonFinalizableObjectsInNewHs = newHeapshot.TypesToNonFinalizableObjectsListMap;
			var hashTableAllTypes = new HashSet<long>();
			foreach (var t in allObjectsInOldHs)
				hashTableAllTypes.Add(t.Key);
			foreach (var t in allObjectsInNewHs)
				hashTableAllTypes.Add(t.Key);
			foreach (var typeId in hashTableAllTypes) {
				typesCollection.Add(new TypeChangeInfo {
					TypeId = typeId,
					TypeName = session.GetTypeName(typeId),
					NewObjects = newObjects.ContainsKey(typeId) ? newObjects[typeId] : EmptyList,
					NewFinalizableObjects = newFinalizableObjects.ContainsKey(typeId) ? newFinalizableObjects[typeId] : EmptyList,
					DeadObjects = deleted.ContainsKey(typeId) ? deleted[typeId] : EmptyList,
					OldHsObjects = allObjectsInOldHs.ContainsKey(typeId) ? allObjectsInOldHs[typeId] : EmptyList,
					NewHsObjects = allObjectsInNewHs.ContainsKey(typeId) ? allObjectsInNewHs[typeId] : EmptyList,
					FinalizableObjects = allFinalizableObjectsInNewHs.ContainsKey(typeId) ? allFinalizableObjectsInNewHs[typeId] : EmptyList,
					NonFinalizableObjects = allNonFinalizableObjectsInNewHs.ContainsKey(typeId) ? allNonFinalizableObjectsInNewHs[typeId] : EmptyList,
				});
			}
			filterTypesTextBox = new TextBox();
			filterTypesTextBox.TextChanged += FilterTypesTextBox_TextChanged;
			CreateTypesView();
			var filterAndTypesStackLayout = new StackLayout();
			filterAndTypesStackLayout.Items.Add(new StackLayoutItem(filterTypesTextBox, HorizontalAlignment.Stretch));
			filterAndTypesStackLayout.Items.Add(new StackLayoutItem(typesGrid, HorizontalAlignment.Stretch, true));

			Content = filterAndTypesStackLayout;
		}

		void FilterTypesTextBox_TextChanged(object sender, EventArgs e)
		{
			var typeNameFilter = filterTypesTextBox.Text;
			if (string.IsNullOrWhiteSpace(typeNameFilter))
				typesCollection.Filter = null;
			else
				typesCollection.Filter = (i) => i.TypeName.IndexOf(typeNameFilter, StringComparison.OrdinalIgnoreCase) != -1;
		}

		GridView typesGrid;
		void CreateTypesView()
		{
			typesGrid = new GridView {
				DataStore = typesCollection
			};
			typesGrid.AllowMultipleSelection = false;
			typesCollection.Sort = (x, y) => (y.NewObjects.Size - y.DeadObjects.Size).CompareTo(x.NewObjects.Size - x.DeadObjects.Size);
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => (r.NewObjects.Count - r.DeadObjects.Count).ToString()) },
				HeaderText = "Diff"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => PrettyPrint.PrintBytes(r.NewObjects.Size - r.DeadObjects.Size)) },
				HeaderText = "Diff Size"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.NonFinalizableObjects.Count.ToString()) },
				HeaderText = "Objects"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.FinalizableObjects.Count.ToString()) },
				HeaderText = "Finalizable Objects"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.NewObjects.Count.ToString()) },
				HeaderText = "New Objects"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.NewFinalizableObjects.Count.ToString()) },
				HeaderText = "New Finalizable Objects"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.DeadObjects.Count.ToString()) },
				HeaderText = "Dead Objects"
			});
			typesGrid.Columns.Add(new GridColumn {
				DataCell = new TextBoxCell { Binding = Binding.Delegate<TypeChangeInfo, string>(r => r.TypeName) },
				HeaderText = "Type Name"
			});
			typesGrid.ContextMenu = CreateContextMenu();
		}

		ContextMenu CreateContextMenu()
		{
			var newObjs = CreateCommand("Select New objects", newHeapshot, tci => tci.NewObjects);
			var deadObjs = CreateCommand("Select Dead objects", oldHeapshot, tci => tci.DeadObjects);
			var newHs = CreateCommand("Select All in New Heapshot", newHeapshot, tci => tci.NewHsObjects);
			var oldHs = CreateCommand("Select All in Old Heapshot", oldHeapshot, tci => tci.OldHsObjects);

			return new ContextMenu(newObjs, deadObjs, newHs, oldHs);
		}

		Command CreateCommand(string text, Heapshot inHeapshot, Func<TypeChangeInfo, LazyObjectsList> objectsToShow)
		{
			var cmd = new Command() {
				MenuText = text,
			};

			cmd.Executed += (sender, e) => {
				if (typesGrid.SelectedItem == null) {
					MessageBox.Show("Select item in list before right-clicking(I know, I know)...");
					return;
				}

				var tci = (TypeChangeInfo)typesGrid.SelectedItem;

				InsertTab(new ObjectListTab(
					session,
					inHeapshot,
					new Dictionary<long, LazyObjectsList>() {
						{ ((TypeChangeInfo)typesGrid.SelectedItem).TypeId, objectsToShow(tci) }
					}),
					this
				);
			};

			return cmd;
		}
	}
}

