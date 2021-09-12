using System.Collections.ObjectModel;
namespace DaemonKit.Core {

    public class ProcessItem {

        public ProcessItem Parent { get; set; }
        public bool IsSuperRoot { get => Parent == null; }
        public ProcessItem () {
            this.Childs = new ObservableCollection<ProcessItem> ();
        }

        public string Name { get; set; }
        public ObservableCollection<ProcessItem> Childs { set; get; }

        public void AddChild (ProcessItem InChild) {
            InChild.Parent = this;
            Childs.Add (InChild);
        }

    }
}