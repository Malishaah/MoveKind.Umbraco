// /Composition/MemberFavoritesComposer.cs
using System;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

public class MemberFavoritesComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder.Components().Append<MemberFavoritesComponent>();
}

public class MemberFavoritesComponent : IComponent
{
    private readonly IRelationService _relationService;

    public MemberFavoritesComponent(IRelationService relationService)
        => _relationService = relationService;

    public void Initialize()
    {
        const string alias = "memberFavoritesWorkout";

        var existing = _relationService.GetRelationTypeByAlias(alias);
        if (existing is null)
        {
            // OBS: både parentObjectType och childObjectType krävs
            var relationType = new RelationType(
                name: "Member favorites Workout",
                alias: alias,
                isBidrectional: false,
                parentObjectType: Constants.ObjectTypes.Member,
                childObjectType: Constants.ObjectTypes.Document, // byt till Media om dina workouts är media
                isDependency: false
            );

            _relationService.Save(relationType); // <-- korrekt metod, inte SaveRelationType
        }
    }

    public void Terminate() { }
}
