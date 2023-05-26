﻿namespace StockSharp.Algo.Strategies.Testing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ecng.Common;

using GeneticSharp;

using StockSharp.Algo.Storages;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// The genetic optimizer of strategies.
/// </summary>
public class GeneticOptimizer : BaseOptimizer
{
	private class StrategyFitness : IFitness
	{
		private readonly GeneticOptimizer _optimizer;
		private readonly Strategy _strategy;
		private readonly Func<Strategy, decimal> _calcFitness;
		private readonly int _iterCount;

		public StrategyFitness(GeneticOptimizer optimizer, Strategy strategy, Func<Strategy, decimal> calcFitness, int iterCount)
		{
			_optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
			_strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
			_calcFitness = calcFitness ?? throw new ArgumentNullException(nameof(calcFitness));
			_iterCount = iterCount;
		}

		double IFitness.Evaluate(IChromosome chromosome)
		{
			var spc = (StrategyParametersChromosome)chromosome;
			var strategy = _strategy.Clone();

			foreach (var gene in spc.GetGenes())
			{
				var (param, value) = ((IStrategyParam, object))gene.Value;
				_strategy.Parameters[param.Id].Value = value;
			}

			using var wait = new ManualResetEvent(false);

			var adapterCache = _optimizer.AllocateAdapterCache();
			var storageCache = _optimizer.AllocateStorageCache();

			_optimizer.TryNextRun(
				() => (strategy, null),
				adapterCache,
				storageCache,
				_iterCount,
				() =>
				{
					_optimizer.FreeAdapterCache(adapterCache);
					_optimizer.FreeStorageCache(storageCache);

					wait.Set();
				});

			wait.WaitOne();

			return (double)_calcFitness(strategy);
		}
	}

	private class StrategyParametersChromosome : ChromosomeBase
	{
		private readonly (IStrategyParam param, object from, object to, int precision)[] _parameters;

		public StrategyParametersChromosome((IStrategyParam param, object from, object to, int precision)[] parameters)
			: base(parameters.CheckOnNull(nameof(parameters)).Length)
		{
			_parameters = parameters;

			for (var i = 0; i < Length; i++)
			{
				ReplaceGene(i, GenerateGene(i));
			}
		}

		public override IChromosome CreateNew()
			=> new StrategyParametersChromosome(_parameters);

		private static decimal GetDecimal(decimal min, decimal max, int precision)
		{
			var value = RandomGen.GetDouble() * ((double)max - (double)min) + (double)min;
			return (decimal)value.Round(precision);
		}

		public override Gene GenerateGene(int geneIndex)
		{
			var (p, f, t, precision) = _parameters[geneIndex];

			object v;

			if (p.Type == typeof(Security))
			{
				v = RandomGen.GetElement((IEnumerable<Security>)f);
			}
			else if (p.Type == typeof(Unit))
			{
				var fu = (Unit)f;
				var tu = (Unit)f;

				v = new Unit(GetDecimal(fu.Value, tu.Value, precision), fu.Type);
			}
			else if (p.Type == typeof(decimal))
			{
				v = GetDecimal(f.To<decimal>(), t.To<decimal>(), precision).To(p.Type);
			}
			else if (p.Type.IsPrimitive())
			{
				v = RandomGen.GetLong(f.To<long>(), t.To<long>()).To(p.Type);
			}
			else
				throw new NotSupportedException($"Type {p.Type} not supported.");

			return new((p, v));
		}
	}

	private GeneticAlgorithm _ga;

	/// <summary>
	/// Initializes a new instance of the <see cref="GeneticOptimizer"/>.
	/// </summary>
	/// <param name="securityProvider">The provider of information about instruments.</param>
	/// <param name="portfolioProvider">The portfolio to be used to register orders. If value is not given, the portfolio with default name Simulator will be created.</param>
	/// <param name="storageRegistry">Market data storage.</param>
	public GeneticOptimizer(ISecurityProvider securityProvider, IPortfolioProvider portfolioProvider, IStorageRegistry storageRegistry)
		: base(securityProvider, portfolioProvider, storageRegistry.CheckOnNull(nameof(storageRegistry)).ExchangeInfoProvider, storageRegistry, StorageFormats.Binary, storageRegistry.DefaultDrive)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="GeneticOptimizer"/>.
	/// </summary>
	/// <param name="securityProvider">The provider of information about instruments.</param>
	/// <param name="portfolioProvider">The portfolio to be used to register orders. If value is not given, the portfolio with default name Simulator will be created.</param>
	/// <param name="exchangeInfoProvider">Exchanges and trading boards provider.</param>
	/// <param name="storageRegistry">Market data storage.</param>
	/// <param name="storageFormat">The format of market data. <see cref="StorageFormats.Binary"/> is used by default.</param>
	/// <param name="drive">The storage which is used by default. By default, <see cref="IStorageRegistry.DefaultDrive"/> is used.</param>
	public GeneticOptimizer(ISecurityProvider securityProvider, IPortfolioProvider portfolioProvider, IExchangeInfoProvider exchangeInfoProvider, IStorageRegistry storageRegistry, StorageFormats storageFormat, IMarketDataDrive drive)
		: base(securityProvider, portfolioProvider, exchangeInfoProvider, storageRegistry, storageFormat, drive)
	{
	}

	/// <summary>
	/// <see cref="GeneticSettings"/>
	/// </summary>
	public GeneticSettings Settings { get; } = new();

	/// <summary>
	/// Start optimization.
	/// </summary>
	/// <param name="strategy">Strategy.</param>
	/// <param name="parameters">Parameters used to generate chromosomes.</param>
	/// <param name="iterationCount"></param>
	/// <param name="calcFitness">Calc fitness value function.</param>
	/// <param name="selection"><see cref="ISelection"/>. If <see langword="null"/> the value from <see cref="GeneticSettings.Selection"/> will be used.</param>
	/// <param name="crossover"><see cref="ICrossover"/>. If <see langword="null"/> the value from <see cref="GeneticSettings.Crossover"/> will be used.</param>
	/// <param name="mutation"><see cref="IMutation"/>. If <see langword="null"/> the value from <see cref="GeneticSettings.Mutation"/> will be used.</param>
	[CLSCompliant(false)]
	public void Start(
		Strategy strategy,
		IEnumerable<(IStrategyParam param, object from, object to, int precision)> parameters,
		int iterationCount,
		Func<Strategy, decimal> calcFitness,
		ISelection selection = default,
		ICrossover crossover = default,
		IMutation mutation = default
	)
	{
		if (parameters is null)
			throw new ArgumentNullException(nameof(parameters));

		if (calcFitness is null)
			throw new ArgumentNullException(nameof(calcFitness));

		if (_ga is not null)
			throw new InvalidOperationException("Not stopped.");

		OnStart(ref iterationCount);

		var population = new Population(Settings.PopulationSize, Settings.PopulationSizeMaximum, new StrategyParametersChromosome(parameters.ToArray()));

		selection ??= Settings.Selection.CreateInstance<ISelection>();
		crossover ??= Settings.Crossover.CreateInstance<ICrossover>();
		mutation ??= Settings.Mutation.CreateInstance<IMutation>();

		_ga = new(population, new StrategyFitness(this, strategy, calcFitness, iterationCount), selection, crossover, mutation)
		{
			TaskExecutor = new ParallelTaskExecutor
			{
				MinThreads = 1,
				MaxThreads = EmulationSettings.BatchSize,
			},

			Termination = new OrTermination(
				new FitnessStagnationTermination(Settings.StagnationGenerations),
				new GenerationNumberTermination(iterationCount)
			),

			MutationProbability = Settings.MutationProbability,
			CrossoverProbability = Settings.CrossoverProbability,

			Reinsertion = Settings.Reinsertion.CreateInstance<IReinsertion>(),
		};

		_ga.GenerationRan += OnGenerationRan;
		_ga.TerminationReached += OnTerminationReached;

		Task.Run(async () =>
		{
			await Task.Yield();
			_ga.Start();
		});
	}

	private void OnTerminationReached(object sender, EventArgs e)
	{
		// TODO

		if (State != ChannelStates.Stopping)
			State = ChannelStates.Stopping;

		State = ChannelStates.Stopped;
	}

	private void OnGenerationRan(object sender, EventArgs e)
	{

	}

	/// <inheritdoc />
	public override void Suspend()
	{
		_ga.Stop();
		base.Suspend();
	}

	/// <inheritdoc />
	public override void Resume()
	{
		_ga.Resume();
		base.Resume();
	}

	/// <inheritdoc />
	public override void Stop()
	{
		_ga.Stop();
		base.Stop();
	}
}