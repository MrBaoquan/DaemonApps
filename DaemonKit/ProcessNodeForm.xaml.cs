﻿using System;
using System.ComponentModel;
using ReactiveUI;

namespace DaemonKit {
    /// <summary>
    /// ProceeNodeForm.xaml 的交互逻辑
    /// </summary>
    public partial class ProcessNodeForm : ReactiveWindow<PNFViewModel> {
        public ProcessNodeForm () {
            InitializeComponent ();

            ViewModel = new PNFViewModel ();
            DataContext = ViewModel;
            this.Topmost = true;

            this.WhenActivated (_disposables => {
                ViewModel.Cancel.Subscribe (_ => {
                    this.Hide ();
                });
            });
        }

        public PNFViewModel VM {
            get { return DataContext as PNFViewModel; }
        }

        protected override void OnClosing (CancelEventArgs e) {
            base.OnClosing (e);
            this.Hide ();
            e.Cancel = true;
        }

    }

}