using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VortexEditor.Models;
using VortexEngine;
using VortexEngine.Components;

namespace VortexEditor.Views;

public partial class MainWindow : Window
{
    private Engine? engine;
    private EditorViewModel? viewModel;
    private EntityId? selectedEntity;
    
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        
        engine = new Engine();
        viewModel = new EditorViewModel(engine);
        DataContext = viewModel;

        RefreshSceneTree();
    }

    private void OnAddEntity_Click(object? sender, RoutedEventArgs e)
    {
        if (engine == null) return;

        var entity = engine.World.CreateEntity();
        engine.World.AddComponent(entity, new Transform());
        engine.World.AddComponent(entity, new Camera());

        RefreshSceneTree();
    }

    private void RefreshSceneTree()
    {
        if (engine == null) return;
        
        var treeList = this.FindControl<ListBox>("SceneTreeList");
        if (treeList == null) return;

        var items = new List<EntityTreeItem>();

        foreach (var entity in engine.World.EntitiesWith<Transform>())
        {
            items.Add(new EntityTreeItem
            {
                Id = entity.Value,
                Name = $"Entity_{entity.Value}"
            });
        }

        treeList.ItemsSource = items;
        treeList.SelectionChanged += (s, e) =>
        {
            if (treeList.SelectedItem is EntityTreeItem item)
            {
                ShowInspector(new EntityId(item.Id));
            }
        };
    }

    private void ShowInspector(EntityId entity)
    {
        if (engine == null) return;

        selectedEntity = entity;
        var inspectorContent = this.FindControl<StackPanel>("InspectorContent");
        if (inspectorContent == null) return;
        
        inspectorContent.Children.Clear();

        if (engine.World.TryGetComponent<Transform>(entity, out var transform))
        {
            var title = new TextBlock
            {
                Text = "Transform",
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Avalonia.Media.Brushes.White,
                Margin = new Avalonia.Thickness(0, 0, 0, 8)
            };
            inspectorContent.Children.Add(title);
            
            var posPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            posPanel.Children.Add(new TextBlock { Text = "Position: ", Foreground = Avalonia.Media.Brushes.White, Width = 80 });
            posPanel.Children.Add(new TextBlock 
            { 
                Text = $"({transform.Position.X:F2}, {transform.Position.Y:F2}, {transform.Position.Z:F2})",
                Foreground = Avalonia.Media.Brushes.LightGray
            });
            inspectorContent.Children.Add(posPanel);
 
            var spacer = new TextBlock { Text = "" };
            inspectorContent.Children.Add(spacer);
        }
        
        if (engine.World.TryGetComponent<Camera>(entity, out var camera))
        {
            var title = new TextBlock 
            { 
                Text = "Camera",
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Avalonia.Media.Brushes.White,
                Margin = new Avalonia.Thickness(0, 8, 0, 8)
            };
            inspectorContent.Children.Add(title);
 
            var fovPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            fovPanel.Children.Add(new TextBlock { Text = "FOV: ", Foreground = Avalonia.Media.Brushes.White, Width = 80 });
            fovPanel.Children.Add(new TextBlock 
            { 
                Text = $"{camera.FieldOfView:F1}°",
                Foreground = Avalonia.Media.Brushes.LightGray
            });
            inspectorContent.Children.Add(fovPanel);
        }
    }

    public class EntityTreeItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        
        public override string ToString() => Name;
    }
}