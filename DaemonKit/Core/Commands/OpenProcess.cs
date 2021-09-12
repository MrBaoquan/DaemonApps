using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DaemonKit.Core.Commands {
    public class OpenProcess : ICommand {
        public event EventHandler CanExecuteChanged;

        public bool CanExecute (object args) {
            return true;
        }

        public void Execute (object args) {

        }
    }
}