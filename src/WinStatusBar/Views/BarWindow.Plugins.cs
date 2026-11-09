using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai.Codex;
namespace WinStatusBar.Views;
public sealed partial class BarWindow
{
    private StackPanel? _pluginChoices;
    private ScrollViewer? _pluginScroller;
    private IReadOnlyList<CodexPluginMention>? _plugins;
    private bool _pluginsLoading;
    private void BuildPluginSuggestions(StackPanel composer)
    {
        _pluginChoices=new StackPanel{Spacing=2};
        _pluginScroller=new ScrollViewer{Content=_pluginChoices,MaxHeight=180,Visibility=Visibility.Collapsed};
        composer.Children.Add(_pluginScroller);
    }
    private async void RefreshPluginSuggestions()
    {
        if(_pluginScroller is null||_pluginChoices is null||_panelInput is null)return;
        _pluginScroller.Visibility=Visibility.Collapsed;
        if(IsChatDestination||CurrentTarget()!=WinStatusBar.Core.Ai.AgentPrompt.Target.Codex)return;
        int caret=Math.Clamp(_panelInput.SelectionStart,0,_panelInput.Text.Length);
        var match=Regex.Match(_panelInput.Text[..caret],@"(?<![\w@])@([\w-]*)$");
        if(!match.Success)return;
        if(_plugins is null)
        {
            if(_pluginsLoading)return;_pluginsLoading=true;
            try{_plugins=await CodexComposer.PluginsAsync(CancellationToken.None);}
            catch(Exception ex){SetNote("Plugins do Codex indisponíveis: "+ex.Message);return;}
            finally{_pluginsLoading=false;}
            RefreshPluginSuggestions();return;
        }
        _pluginChoices.Children.Clear();
        foreach(var plugin in _plugins.Where(p=>p.Name.Contains(match.Groups[1].Value,StringComparison.OrdinalIgnoreCase)||p.DisplayName.Contains(match.Groups[1].Value,StringComparison.OrdinalIgnoreCase)).Take(12))
        {
            var button=new Button{Content=$"@{plugin.Name} · {plugin.DisplayName}",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};
            button.Click+=(_,_)=>
            {
                if(_panelInput is null)return;
                string insertion="@"+plugin.Name+" ";
                _panelInput.Text=_panelInput.Text.Remove(match.Index,match.Length).Insert(match.Index,insertion);
                _panelInput.SelectionStart=match.Index+insertion.Length;
                _panelInput.Focus(FocusState.Programmatic);_pluginScroller.Visibility=Visibility.Collapsed;
            };
            _pluginChoices.Children.Add(button);
        }
        _pluginScroller.Visibility=_pluginChoices.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
    }
    /// <summary>Com acesso completo, o próprio UAC do Windows é a confirmação: sem diálogo extra antes dele.</summary>
    private bool FullAccessOn => _settings.Current.ContextActionsEnabled && _allowEdits;
    private Task<bool> ConfirmDesktopElevationAsync(CancellationToken ct)
        => FullAccessOn ? Task.FromResult(true) : ShowApprovalAsync("Permissão de administrador", "A tarefa precisa de acesso elevado. Autorizar o auxiliar do WinStatusBar a solicitar permissão de administrador pelo UAC? A barra principal continuará sem elevação.",ct);
    private Task<bool> ShowApprovalAsync(string title,string text,CancellationToken ct)
    {
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(async()=>
        {
            ContentDialog? dialog=null;
            using var registration=ct.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog?.Hide()));
            try
            {
                ct.ThrowIfCancellationRequested();
                dialog=new ContentDialog{XamlRoot=_panelInput?.XamlRoot??Root.XamlRoot,Title=title,
                    Content=new ScrollViewer{MaxHeight=360,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap}},
                    PrimaryButtonText="Autorizar",CloseButtonText="Cancelar",DefaultButton=ContentDialogButton.Close};
                completion.TrySetResult(await dialog.ShowAsync()==ContentDialogResult.Primary&&!ct.IsCancellationRequested);
            }
            catch(Exception ex){completion.TrySetException(ex);}
        }))completion.TrySetException(new InvalidOperationException("A janela foi fechada."));
        return completion.Task.WaitAsync(ct);
    }
    /// <summary>
    /// O Claude no chat da barra roda sem pedido de permissão interativo; quando ele tenta usar um conector
    /// (Google Drive, SketchUp, plugins…) e é negado, a barra pergunta aqui e repete o pedido com o conector liberado.
    /// </summary>
    private async Task OfferConnectorPermissionAsync(Conversation conv,IReadOnlyList<string> blocked,IReadOnlyList<string> allowedInRun)
    {
        var groups=WinStatusBar.Core.Ai.ConnectorPermissions.Group(blocked)
            .Where(g=>!allowedInRun.Contains(g.Rule)).ToList(); // bloqueado mesmo liberado = outro motivo: não insiste
        if(groups.Count==0)return;
        if(_settings.Current.ConnectorsFullAccess)
        {
            // Diretriz de acesso total: sem pergunta; libera para sempre e continua.
            foreach(var g in groups){if(!conv.AllowedTools.Contains(g.Rule))conv.AllowedTools.Add(g.Rule);if(!_settings.Current.AllowedConnectors.Contains(g.Rule))_settings.Current.AllowedConnectors.Add(g.Rule);}
            SaveSettingsQuietly();
            App.Log("Conectores liberados automaticamente (acesso total): "+string.Join(", ",groups.Select(g=>g.Name)));
            if(_conv==conv)SubmitCommand("Os conectores "+string.Join(", ",groups.Select(g=>g.Name))+" foram liberados. Continue o pedido anterior.");
            return;
        }
        var completion=new TaskCompletionSource<(bool Ok,bool Always)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(async()=>
        {
            try
            {
                var content=new StackPanel{Spacing=8};
                content.Children.Add(new TextBlock{Text="O Claude tentou usar conectores da sua conta e precisa da sua autorização:",TextWrapping=TextWrapping.Wrap});
                foreach(var g in groups)
                    content.Children.Add(new TextBlock{Text="• "+g.Name+" ("+string.Join(", ",g.Tools)+")",TextWrapping=TextWrapping.Wrap});
                var always=new CheckBox{Content="Sempre permitir estes conectores no chat da barra"};
                content.Children.Add(always);
                content.Children.Add(new TextBlock{Text="Ao autorizar, o pedido continua na mesma conversa com o conector liberado.",TextWrapping=TextWrapping.Wrap,Opacity=0.7});
                var dialog=new ContentDialog{XamlRoot=_panelInput?.XamlRoot??Root.XamlRoot,Title="Usar conectores",Content=new ScrollViewer{MaxHeight=360,Content=content},
                    PrimaryButtonText="Autorizar e continuar",CloseButtonText="Agora não",DefaultButton=ContentDialogButton.Primary};
                bool ok=await dialog.ShowAsync()==ContentDialogResult.Primary;
                completion.TrySetResult((ok,always.IsChecked==true));
            }
            catch(Exception ex){completion.TrySetException(ex);}
        }))return;
        var (accepted,forever)=await completion.Task;
        if(!accepted||_conv!=conv)return;
        foreach(var g in groups)
        {
            if(!conv.AllowedTools.Contains(g.Rule))conv.AllowedTools.Add(g.Rule);
            if(forever&&!_settings.Current.AllowedConnectors.Contains(g.Rule))_settings.Current.AllowedConnectors.Add(g.Rule);
        }
        if(forever)SaveSettingsQuietly();
        App.Log("Conectores autorizados ("+(forever?"sempre":"nesta conversa")+"): "+string.Join(", ",groups.Select(g=>g.Name)));
        SubmitCommand("Autorizei o uso de "+string.Join(", ",groups.Select(g=>g.Name))+". Continue o pedido anterior.");
    }
    private async Task<JsonNode> HandleCodexRequestAsync(string method,JsonElement args,CancellationToken ct)
    {
        // Acesso total a plugins e conectores: ferramentas de MCP/plugins aprovadas sem pergunta (comandos e edições seguem o modo Editar).
        if(_settings.Current.ConnectorsFullAccess&&method.EndsWith("requestApproval",StringComparison.Ordinal)&&(method.Contains("mcp",StringComparison.OrdinalIgnoreCase)||method.Contains("tool",StringComparison.OrdinalIgnoreCase)))
        {
            App.Log("Codex: ferramenta de plugin aprovada automaticamente ("+method+")");
            return new JsonObject{["decision"]="accept"};
        }
        if(_settings.Current.ConnectorsFullAccess&&method=="mcpServer/elicitation/request"&&!HasFormFields(args))
        {
            App.Log("Codex: confirmação de plugin aceita automaticamente");
            return new JsonObject{["action"]="accept",["content"]=new JsonObject()};
        }
        if(method.EndsWith("requestApproval",StringComparison.Ordinal))
        {
            bool accepted=FullAccessOn||_allowEdits&&await ShowApprovalAsync("Codex solicita autorização",args.ToString(),ct);
            return new JsonObject{["decision"]=accepted?"accept":"decline"};
        }
        if(method=="mcpServer/elicitation/request")
        {
            // Auth and arbitrary plugin forms must not be silently accepted or populated by the model.
            await ShowApprovalAsync("Plugin solicita informação",args.ToString()+"\nConclua a autenticação ou configuração no aplicativo do Codex e tente novamente.",ct);
            return new JsonObject{["action"]="cancel"};
        }
        if(method=="item/tool/requestUserInput")return await AnswerCodexQuestionsAsync(args,ct);
        throw new NotSupportedException("Solicitação do Codex ainda não suportada pela barra: "+method);
    }
    /// <summary>Pedido do plugin com campos a preencher (login, formulário): nunca preenchido automaticamente.</summary>
    private static bool HasFormFields(JsonElement args)
        => args.TryGetProperty("requestedSchema",out var schema)&&schema.ValueKind==JsonValueKind.Object
           &&schema.TryGetProperty("properties",out var props)&&props.ValueKind==JsonValueKind.Object&&props.EnumerateObject().Any();
    private Task<JsonNode> AnswerCodexQuestionsAsync(JsonElement args,CancellationToken ct)
    {
        var completion=new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(async()=>
        {
            ContentDialog? dialog=null;
            using var registration=ct.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog?.Hide()));
            try
            {
                ct.ThrowIfCancellationRequested();var content=new StackPanel{Spacing=10};var fields=new Dictionary<string,TextBox>();
                foreach(var question in args.GetProperty("questions").EnumerateArray())
                {
                    var field=new TextBox{Header=question.GetProperty("question").GetString(),TextWrapping=TextWrapping.Wrap};
                    if(question.TryGetProperty("options",out var options)&&options.ValueKind==JsonValueKind.Array)
                        field.PlaceholderText=string.Join(" / ",options.EnumerateArray().Select(o=>o.GetProperty("label").GetString()));
                    fields[question.GetProperty("id").GetString()!]=field;content.Children.Add(field);
                }
                dialog=new ContentDialog{XamlRoot=_panelInput?.XamlRoot??Root.XamlRoot,Title="Pergunta do Codex",Content=new ScrollViewer{Content=content,MaxHeight=400},PrimaryButtonText="Responder",CloseButtonText="Cancelar"};
                var result=await dialog.ShowAsync();var answers=new JsonObject();
                if(result==ContentDialogResult.Primary&&!ct.IsCancellationRequested)foreach(var pair in fields)answers[pair.Key]=new JsonObject{["answers"]=new JsonArray(pair.Value.Text)};
                completion.TrySetResult(new JsonObject{["answers"]=answers});
            }
            catch(Exception ex){completion.TrySetException(ex);}
        }))completion.TrySetException(new InvalidOperationException("A janela foi fechada."));
        return completion.Task.WaitAsync(ct);
    }
}
